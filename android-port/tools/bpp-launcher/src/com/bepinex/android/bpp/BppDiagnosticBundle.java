package com.bepinex.android.bpp;

import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.time.Instant;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Pure-Java diagnostic zip builder. Android collection lives in {@link BppDiagnosticExporter};
 * this type only packs bytes, so a grey-screen report stays readable when a probe fails.
 */
public final class BppDiagnosticBundle {

    public static final int LOG_TAIL_BYTES = 8 * 1024 * 1024;
    public static final int SMALL_TEXT_MAX_BYTES = 2 * 1024 * 1024;
    public static final int TOTAL_STORED_BYTES = 40 * 1024 * 1024;
    public static final int MAX_INCLUDED_FILES = 400;
    public static final int MAX_SCAN_DEPTH = 8;
    public static final int MAX_TREE_LINES = 1500;
    public static final String README_ENTRY = "README.txt";
    public static final String MANIFEST_ENTRY = "manifest.txt";
    public static final String EXPORTER_REVISION = "1";

    private static final int ELF_PROBE_BYTES = 64 * 1024;
    private static final Pattern SENSITIVE_NAME = Pattern.compile(
        "(?i)(^|[^a-z0-9])(keystore|jks|p12|pfx|pem|id_rsa|lineage|password|secret|credential|private[-_]?key)([^a-z0-9]|$)"
            + "|\\.(keystore|jks|p12|pfx|pem)$"
    );
    private static final Pattern PREF_XML_SECRET = Pattern.compile(
        "(?i)(<[^>]*\\bname\\s*=\\s*\"[^\"]*(?:password|secret|token|keystore|credential|lineage)[^\"]*\"[^>]*>)(.*?)(</)",
        Pattern.DOTALL
    );
    private static final Pattern PREF_LINE_SECRET = Pattern.compile(
        "(?i)^(\\s*(?:password|secret|token|keystore|credential|lineage)[A-Za-z0-9_.-]*)(\\s*[=:].*)$"
    );
    private static final Set<String> HEAVY_DIRS = new HashSet<String>(Arrays.asList(
        "plugins", "core", "dotnet", "cache", "il2cpp", "unity", "extracted", "odex", "oat"
    ));

    private BppDiagnosticBundle() {}

    public static final class TextEntry {
        public final String archivePath;
        public final String content;

        public TextEntry(String archivePath, String content) {
            this.archivePath = archivePath;
            this.content = content == null ? "" : content;
        }
    }

    public static final class ScanRoot {
        public final File directory;
        public final String label;
        /** When true, small text and DataStore files in this directory are logs/prefs, not game data. */
        public final boolean stateTree;

        public ScanRoot(File directory, String label) {
            this(directory, label, false);
        }

        public ScanRoot(File directory, String label, boolean stateTree) {
            this.directory = directory;
            this.label = label;
            this.stateTree = stateTree;
        }
    }

    public static final class ExtraFile {
        public final File file;
        public final String archivePath;
        public final boolean logTail;

        public ExtraFile(File file, String archivePath, boolean logTail) {
            this.file = file;
            this.archivePath = archivePath;
            this.logTail = logTail;
        }
    }

    public static final class Request {
        public final String readme;
        public final List<TextEntry> texts;
        public final List<ScanRoot> scans;
        public final List<ExtraFile> extras;

        public Request(String readme, List<TextEntry> texts, List<ScanRoot> scans, List<ExtraFile> extras) {
            this.readme = readme == null ? "" : readme;
            this.texts = texts == null ? new ArrayList<TextEntry>() : texts;
            this.scans = scans == null ? new ArrayList<ScanRoot>() : scans;
            this.extras = extras == null ? new ArrayList<ExtraFile>() : extras;
        }
    }

    public static final class Result {
        public final int includedFiles;
        public final int unavailable;
        public final int omitted;
        public final long storedBytes;

        Result(int includedFiles, int unavailable, int omitted, long storedBytes) {
            this.includedFiles = includedFiles;
            this.unavailable = unavailable;
            this.omitted = omitted;
            this.storedBytes = storedBytes;
        }
    }

    public static String nowIso() {
        return DateTimeFormatter.ISO_INSTANT.format(Instant.now());
    }

    public static String readme(String generatedAt, String launcherVersion, String packageName) {
        return ""
            + "BazaarPlusPlus Android 诊断包\n"
            + "================================\n"
            + "生成时间：" + safe(generatedAt) + "\n"
            + "启动器：" + safe(launcherVersion) + "\n"
            + "包名：" + safe(packageName) + "\n"
            + "\n"
            + "这个压缩包由启动器首页「导出诊断日志」生成，用来反馈灰屏、闪退、卡在启动或模组没有生效。\n"
            + "导出后会保存到系统「下载」，并可通过系统分享面板发出。请把整个 zip 发给维护者，或附在 GitHub Issue。\n"
            + "\n"
            + "包里可能有设备型号、系统指纹、应用路径、日志里的对局编号和 ANDROID_ID。\n"
            + "不包含：对局历史数据库、录像、插件 DLL / SO 本体、签名私钥、keystore、口令。\n"
            + "配置里名字像 password、token、secret 的值会打成 [redacted]。\n"
            + "\n"
            + "建议复现方式：\n"
            + "1. 打开 BPP Android（此时开始滚动记录 logcat）。\n"
            + "2. 点击「启动 The Bazaar（BPP 模组）」，等到灰屏或闪退。\n"
            + "3. 回到启动器，再点「导出诊断日志」。\n"
            + "上次启动会留在 launch/。若系统把启动器进程杀掉，灰屏后半段只保留游戏进程里已经写盘的记录。\n"
            + "\n"
            + "目录：\n"
            + "- README.txt       本说明\n"
            + "- manifest.txt     已收入、被截断、读不到、主动跳过的清单。沙箱不允许的路径记在这里，而不是让启动器崩溃\n"
            + "- device/          厂商、机型、指纹、Android / SDK、ABI、页大小、安全补丁、内存、存储、屏幕与刘海、getprop\n"
            + "- packages/        启动器与 The Bazaar 的版本、安装时间、签名证书 SHA-256、分包 APK、libil2cpp 大小、哈希和 ELF 对齐\n"
            + "- deploy/          内置模组部署状态、状态文件、目录树、清单\n"
            + "- launch/          每次点击启动的记录，以及启动器进程和 :game 进程的滚动 logcat\n"
            + "- logcat/          点导出时能读到的各 buffer 转储\n"
            + "- logs/            BepInEx LogOutput、preloader、BPP trace、Unity Player.log、配置、可读 tombstone\n"
            + "- crashes/         getHistoricalProcessExitReasons（含 :game）和 trace；系统 tombstone 目录若不可读会记在 manifest\n"
            + "- state/           启动器 SharedPreferences 与 DataStore\n"
            + "\n"
            + "manifest.txt 的 [unavailable] 表示当时读不到，不代表手机上一定没有这个文件。\n"
            + "大日志只保留末尾一段，并在文件头写明 originalBytes。\n"
            + "\n"
            + "English: one shareable zip of device, package, modpack deploy state, rolling launch logcat,\n"
            + "BepInEx/Unity logs, exit reasons, and launcher prefs. Databases and signing secrets are omitted.\n"
            + "Failures are listed in manifest.txt. The launcher keeps a rolling logcat so a grey screen can be\n"
            + "exported after returning to this app.\n";
    }

    public static List<String> rollingLogcatCommand(File outputFile) {
        String path = outputFile == null ? "bpp-logcat" : outputFile.getAbsolutePath();
        return Arrays.asList(
            "logcat",
            "-v", "threadtime",
            "-b", "main",
            "-b", "system",
            "-b", "crash",
            "-b", "events",
            "-f", path,
            "-r", "4096",
            "-n", "4"
        );
    }

    public static final String[] LOGCAT_BUFFERS = new String[] {
        "main", "system", "crash", "events", "radio", "kernel", "security"
    };

    public static List<String> dumpLogcatCommand(String buffer) {
        if (buffer == null || buffer.length() == 0 || "all".equals(buffer)) {
            return Arrays.asList("logcat", "-d", "-v", "threadtime", "-b", "all");
        }
        return Arrays.asList("logcat", "-d", "-v", "threadtime", "-b", buffer);
    }

    public static boolean commandClearsLogcat(List<String> command) {
        if (command == null) return false;
        for (int i = 0; i < command.size(); i++) {
            String arg = command.get(i);
            if ("-c".equals(arg) || "--clear".equals(arg)) return true;
        }
        return false;
    }

    public static String captureProcessText(List<String> command, long timeoutMs, int maxBytes) {
        try {
            byte[] data = captureProcess(command, timeoutMs, maxBytes);
            return new String(data, StandardCharsets.UTF_8);
        } catch (Exception ex) {
            return "[unavailable] " + ex.getClass().getName() + ": " + ex.getMessage() + "\n";
        }
    }

    public static byte[] captureProcess(List<String> command, long timeoutMs, int maxBytes)
        throws IOException, InterruptedException {
        if (command == null || command.isEmpty()) {
            throw new IOException("empty command");
        }
        if (commandClearsLogcat(command)) {
            throw new IOException("refusing logcat -c");
        }
        ProcessBuilder pb = new ProcessBuilder(command);
        pb.redirectErrorStream(true);
        final Process process = pb.start();
        final ByteArrayOutputStream bos = new ByteArrayOutputStream();
        final int limit = Math.max(0, maxBytes);
        Thread reader = new Thread(new Runnable() {
            @Override
            public void run() {
                byte[] buf = new byte[8192];
                try {
                    InputStream in = process.getInputStream();
                    int n;
                    while ((n = in.read(buf)) != -1) {
                        synchronized (bos) {
                            if (bos.size() < limit) {
                                bos.write(buf, 0, Math.min(n, limit - bos.size()));
                            }
                        }
                    }
                } catch (IOException ignored) {
                }
            }
        }, "bpp-diag-read");
        reader.setDaemon(true);
        reader.start();
        boolean finished = process.waitFor(Math.max(1L, timeoutMs), TimeUnit.MILLISECONDS);
        if (!finished) {
            process.destroyForcibly();
        }
        reader.join(2000L);
        if (process.isAlive()) {
            process.destroyForcibly();
        }
        synchronized (bos) {
            return bos.toByteArray();
        }
    }

    public static void appendCapped(File file, String line, int maxBytes) throws IOException {
        if (file == null) throw new IOException("missing append target");
        String payload = line == null ? "\n" : (line.endsWith("\n") ? line : line + "\n");
        byte[] incoming = payload.getBytes(StandardCharsets.UTF_8);
        int cap = Math.max(incoming.length, maxBytes);
        File parent = file.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            throw new IOException("cannot create " + parent.getAbsolutePath());
        }
        synchronized (BppDiagnosticBundle.class) {
            try (RandomAccessFile raf = new RandomAccessFile(file, "rw");
                 FileChannel channel = raf.getChannel();
                 FileLock ignored = channel.lock()) {
                int existingLength = (int) Math.min(raf.length(), cap);
                byte[] existing = new byte[existingLength];
                if (existingLength > 0 && raf.length() > cap) {
                    raf.seek(raf.length() - existingLength);
                } else {
                    raf.seek(0L);
                }
                raf.readFully(existing);
                byte[] combined = new byte[existing.length + incoming.length];
                System.arraycopy(existing, 0, combined, 0, existing.length);
                System.arraycopy(incoming, 0, combined, existing.length, incoming.length);
                byte[] stored = combined;
                if (combined.length > cap) {
                    int start = combined.length - cap;
                    int newline = -1;
                    int scanEnd = Math.min(combined.length - 1, start + 4096);
                    for (int i = start; i < scanEnd; i++) {
                        if (combined[i] == '\n') {
                            newline = i + 1;
                            break;
                        }
                    }
                    if (newline > 0 && newline < combined.length) start = newline;
                    stored = new byte[combined.length - start];
                    System.arraycopy(combined, start, stored, 0, stored.length);
                }
                raf.seek(0L);
                raf.write(stored);
                raf.setLength(stored.length);
                channel.force(true);
            }
        }
    }

    public static boolean isSensitivePath(String path) {
        if (path == null || path.length() == 0) return false;
        String normalized = path.replace('\\', '/');
        int slash = normalized.lastIndexOf('/');
        String name = slash >= 0 ? normalized.substring(slash + 1) : normalized;
        return SENSITIVE_NAME.matcher(name).find() || SENSITIVE_NAME.matcher(normalized).find();
    }

    public static String redactPreferenceText(String text) {
        if (text == null || text.length() == 0) return "";
        Matcher xml = PREF_XML_SECRET.matcher(text);
        StringBuffer xmlOut = new StringBuffer();
        while (xml.find()) {
            xml.appendReplacement(xmlOut, Matcher.quoteReplacement(xml.group(1) + "[redacted]" + xml.group(3)));
        }
        xml.appendTail(xmlOut);
        String[] lines = xmlOut.toString().split("\n", -1);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < lines.length; i++) {
            Matcher line = PREF_LINE_SECRET.matcher(lines[i]);
            if (line.find()) {
                sb.append(line.group(1)).append("=[redacted]");
            } else {
                sb.append(lines[i]);
            }
            if (i + 1 < lines.length) sb.append('\n');
        }
        return sb.toString();
    }

    public static String sanitizeArchivePath(String raw) {
        if (raw == null) return null;
        String path = raw.replace('\\', '/').trim();
        while (path.startsWith("/")) path = path.substring(1);
        if (path.length() == 0) return null;
        String[] parts = path.split("/");
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < parts.length; i++) {
            String part = parts[i];
            if (part.length() == 0 || ".".equals(part) || "..".equals(part)) return null;
            if (part.indexOf('\0') >= 0) return null;
            if (sb.length() > 0) sb.append('/');
            sb.append(part);
        }
        String cleaned = sb.toString();
        if (README_ENTRY.equals(cleaned) || MANIFEST_ENTRY.equals(cleaned)) return null;
        return cleaned;
    }

    public static String describeElfLoadSegments(byte[] data, String source) {
        StringBuilder sb = new StringBuilder();
        sb.append("source=").append(source == null ? "" : source).append('\n');
        if (data == null || data.length < 64
            || data[0] != 0x7f || data[1] != 'E' || data[2] != 'L' || data[3] != 'F') {
            sb.append("elf=unrecognized\n");
            return sb.toString();
        }
        int cls = data[4] & 0xff;
        int encoding = data[5] & 0xff;
        sb.append("class=").append(cls).append(" dataEncoding=").append(encoding).append('\n');
        ByteOrder order = encoding == 2 ? ByteOrder.BIG_ENDIAN : ByteOrder.LITTLE_ENDIAN;
        ByteBuffer buf = ByteBuffer.wrap(data).order(order);
        try {
            long minAlign = Long.MAX_VALUE;
            if (cls == 2) {
                long phoff = buf.getLong(32);
                int phentsize = buf.getShort(54) & 0xffff;
                int phnum = buf.getShort(56) & 0xffff;
                sb.append("phoff=").append(phoff)
                    .append(" phentsize=").append(phentsize)
                    .append(" phnum=").append(phnum).append('\n');
                minAlign = appendLoadSegments(sb, buf, data.length, phoff, phentsize, phnum, true);
            } else if (cls == 1) {
                long phoff = buf.getInt(28) & 0xffffffffL;
                int phentsize = buf.getShort(42) & 0xffff;
                int phnum = buf.getShort(44) & 0xffff;
                sb.append("phoff=").append(phoff)
                    .append(" phentsize=").append(phentsize)
                    .append(" phnum=").append(phnum).append('\n');
                minAlign = appendLoadSegments(sb, buf, data.length, phoff, phentsize, phnum, false);
            } else {
                sb.append("elf=unknown-class\n");
            }
            if (minAlign != Long.MAX_VALUE) sb.append("minLoadAlign=").append(minAlign).append('\n');
        } catch (RuntimeException ex) {
            sb.append("elfError=").append(ex).append('\n');
        }
        return sb.toString();
    }

    public static String renderTree(File root, int maxLines, int maxDepth) {
        StringBuilder sb = new StringBuilder();
        if (root == null || !root.exists()) {
            return "(missing)\n";
        }
        if (!root.canRead()) {
            return "(unreadable) " + root.getAbsolutePath() + "\n";
        }
        int[] lines = new int[] {0};
        boolean[] truncated = new boolean[] {false};
        walkTree(root, root, 0, Math.max(1, maxDepth), Math.max(1, maxLines), lines, truncated, sb);
        if (truncated[0]) sb.append("... truncated\n");
        return sb.toString();
    }

    public static String sha256Hex(File file, long maxBytes) throws IOException {
        MessageDigest digest = newSha256();
        long total = 0L;
        try (InputStream in = new java.io.FileInputStream(file)) {
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) != -1) {
                total += n;
                if (maxBytes >= 0 && total > maxBytes) {
                    throw new IOException("hash cap exceeded");
                }
                digest.update(buf, 0, n);
            }
        }
        return toHex(digest.digest());
    }

    public static Result write(File zipFile, Request request) throws IOException {
        return write(zipFile, request, LOG_TAIL_BYTES, SMALL_TEXT_MAX_BYTES, TOTAL_STORED_BYTES, MAX_INCLUDED_FILES);
    }

    static Result write(
        File zipFile,
        Request request,
        int logTailBytes,
        int smallTextMax,
        int totalCap,
        int maxFiles
    ) throws IOException {
        if (zipFile == null) throw new IOException("missing zip destination");
        if (request == null) throw new IOException("missing diagnostic request");
        List<String> unavailable = new ArrayList<String>();
        List<String> omitted = new ArrayList<String>();
        List<Slice> slices = new ArrayList<Slice>();
        Set<String> seenFiles = new HashSet<String>();
        Set<String> usedPaths = new HashSet<String>();
        Budget budget = new Budget(Math.max(0, totalCap), Math.max(0, maxFiles));
        int[] skippedPolicy = new int[] {0};

        for (int i = 0; i < request.texts.size(); i++) {
            TextEntry text = request.texts.get(i);
            addInline(slices, usedPaths, unavailable, omitted, budget, text, smallTextMax);
        }
        for (int i = 0; i < request.scans.size(); i++) {
            scanRoot(request.scans.get(i), slices, usedPaths, seenFiles, unavailable, omitted,
                budget, skippedPolicy, logTailBytes, smallTextMax);
        }
        for (int i = 0; i < request.extras.size(); i++) {
            ExtraFile extra = request.extras.get(i);
            addExtra(extra, slices, usedPaths, seenFiles, unavailable, omitted, budget, logTailBytes, smallTextMax);
        }
        if (skippedPolicy[0] > 0) {
            omitted.add("summary\tskipped-by-policy\t" + skippedPolicy[0]);
        }

        File parent = zipFile.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            throw new IOException("cannot create " + parent.getAbsolutePath());
        }
        long stored = 0L;
        List<String> includedFinal = new ArrayList<String>();
        try (ZipOutputStream zos = new ZipOutputStream(new BufferedOutputStream(new FileOutputStream(zipFile)))) {
            byte[] readmeBytes = request.readme.getBytes(StandardCharsets.UTF_8);
            putBytes(zos, README_ENTRY, readmeBytes, System.currentTimeMillis());
            stored += readmeBytes.length;
            for (int i = 0; i < slices.size(); i++) {
                Slice slice = slices.get(i);
                try {
                    Stored storedEntry = writeSlice(zos, slice);
                    stored += storedEntry.storedBytes;
                    includedFinal.add(slice.archivePath
                        + "\t" + slice.originalSize
                        + "\t" + storedEntry.storedBytes
                        + "\t" + storedEntry.sha256
                        + "\t" + slice.note);
                } catch (IOException ex) {
                    unavailable.add(slice.archivePath + "\twrite-failed\t" + oneLine(ex.toString()));
                }
            }
            String manifest = renderManifest(includedFinal, unavailable, omitted, stored);
            byte[] manifestBytes = manifest.getBytes(StandardCharsets.UTF_8);
            putBytes(zos, MANIFEST_ENTRY, manifestBytes, System.currentTimeMillis());
            stored += manifestBytes.length;
        }
        return new Result(includedFinal.size(), unavailable.size(), omitted.size(), stored);
    }

    private static void addInline(
        List<Slice> slices,
        Set<String> usedPaths,
        List<String> unavailable,
        List<String> omitted,
        Budget budget,
        TextEntry text,
        int smallTextMax
    ) {
        if (text == null) return;
        String path = uniquePath(usedPaths, text.archivePath);
        if (path == null) {
            unavailable.add(String.valueOf(text.archivePath) + "\trejected-archive-path");
            return;
        }
        if (isSensitivePath(path)) {
            omitted.add(path + "\tsensitive-name");
            return;
        }
        byte[] raw = text.content.getBytes(StandardCharsets.UTF_8);
        String note = "inline";
        if (isPreferencePath(path)) {
            raw = redactPreferenceText(new String(raw, StandardCharsets.UTF_8)).getBytes(StandardCharsets.UTF_8);
            note = "inline-redacted";
        }
        long original = raw.length;
        if (smallTextMax > 0 && raw.length > smallTextMax) {
            byte[] head = new byte[smallTextMax];
            System.arraycopy(raw, 0, head, 0, smallTextMax);
            raw = head;
            note = note + "-head";
        }
        if (!budget.take(raw.length)) {
            omitted.add(path + "\tskipped-over-remaining-budget");
            if (budget.files <= 0) noteBudget(omitted, budget);
            return;
        }
        slices.add(new Slice(path, raw, null, 0L, raw.length, original, note));
    }

    private static void scanRoot(
        ScanRoot root,
        List<Slice> slices,
        Set<String> usedPaths,
        Set<String> seenFiles,
        List<String> unavailable,
        List<String> omitted,
        Budget budget,
        int[] skippedPolicy,
        int logTailBytes,
        int smallTextMax
    ) {
        String label = root == null || root.label == null ? "scan" : root.label;
        if (root == null || root.directory == null) {
            unavailable.add(label + "\tmissing");
            return;
        }
        File dir = root.directory;
        if (!dir.exists()) {
            unavailable.add(label + "\tmissing\t" + dir.getAbsolutePath());
            return;
        }
        if (!dir.canRead()) {
            unavailable.add(label + "\tunreadable\t" + dir.getAbsolutePath());
            return;
        }
        if (!dir.isDirectory()) {
            ExtraFile extra = new ExtraFile(dir, label, true);
            addExtra(extra, slices, usedPaths, seenFiles, unavailable, omitted, budget, logTailBytes, smallTextMax);
            return;
        }
        walk(dir, dir, label, root.stateTree, 0, slices, usedPaths, seenFiles, unavailable, omitted, budget,
            skippedPolicy, logTailBytes, smallTextMax);
    }

    private static void walk(
        File root,
        File dir,
        String label,
        boolean stateTree,
        int depth,
        List<Slice> slices,
        Set<String> usedPaths,
        Set<String> seenFiles,
        List<String> unavailable,
        List<String> omitted,
        Budget budget,
        int[] skippedPolicy,
        int logTailBytes,
        int smallTextMax
    ) {
        if (budget.files <= 0) return;
        if (depth > MAX_SCAN_DEPTH) {
            omitted.add(label + "\tmax-depth\t" + safeRelative(root, dir));
            return;
        }
        File[] children = dir.listFiles();
        if (children == null) {
            unavailable.add(label + "/" + safeRelative(root, dir) + "\tunreadable");
            return;
        }
        Arrays.sort(children, new Comparator<File>() {
            @Override
            public int compare(File a, File b) {
                return a.getName().compareTo(b.getName());
            }
        });
        for (int i = 0; i < children.length; i++) {
            if (budget.files <= 0) return;
            File child = children[i];
            String name = child.getName();
            if (".".equals(name) || "..".equals(name)) continue;
            String relative = safeRelative(root, child);
            if (isSensitivePath(name) || isSensitivePath(relative)) {
                omitted.add(label + "/" + relative + "\tsensitive-name");
                continue;
            }
            if (isSymlinkEscape(root, child)) {
                omitted.add(label + "/" + relative + "\tsymlink-escape");
                continue;
            }
            if (child.isDirectory()) {
                if (HEAVY_DIRS.contains(name) || name.startsWith("staging-")) {
                    omitted.add(label + "/" + relative + "\tskipped-heavy-dir");
                    continue;
                }
                walk(root, child, label, stateTree, depth + 1, slices, usedPaths, seenFiles, unavailable, omitted,
                    budget, skippedPolicy, logTailBytes, smallTextMax);
                continue;
            }
            if (!child.isFile()) {
                skippedPolicy[0]++;
                continue;
            }
            if (isDatabaseName(name)) {
                omitted.add(label + "/" + relative + "\tdatabase\t" + child.length());
                continue;
            }
            if (!shouldInclude(relative, name) && !(stateTree && includeStateFile(name))) {
                skippedPolicy[0]++;
                continue;
            }
            if (!child.canRead()) {
                unavailable.add(label + "/" + relative + "\tunreadable");
                continue;
            }
            String canonical = canonical(child);
            if (canonical != null && !seenFiles.add(canonical)) continue;
            String archive = uniquePath(usedPaths, label + "/" + relative);
            if (archive == null) {
                unavailable.add(label + "/" + relative + "\trejected-archive-path");
                continue;
            }
            addSliceForFile(slices, budget, omitted, child, archive, modeFor(relative, name), logTailBytes, smallTextMax);
        }
    }

    private static void addExtra(
        ExtraFile extra,
        List<Slice> slices,
        Set<String> usedPaths,
        Set<String> seenFiles,
        List<String> unavailable,
        List<String> omitted,
        Budget budget,
        int logTailBytes,
        int smallTextMax
    ) {
        if (extra == null) return;
        String archive = uniquePath(usedPaths, extra.archivePath);
        if (archive == null) {
            unavailable.add(String.valueOf(extra.archivePath) + "\trejected-archive-path");
            return;
        }
        if (extra.file == null || !extra.file.exists()) {
            unavailable.add(archive + "\tmissing");
            return;
        }
        if (isSensitivePath(extra.file.getName()) || isSensitivePath(archive)) {
            omitted.add(archive + "\tsensitive-name");
            return;
        }
        if (!extra.file.canRead() || !extra.file.isFile()) {
            unavailable.add(archive + "\tunreadable");
            return;
        }
        String canonical = canonical(extra.file);
        if (canonical != null && !seenFiles.add(canonical)) return;
        Mode mode = extra.logTail ? Mode.TAIL : modeFor(archive, extra.file.getName());
        addSliceForFile(slices, budget, omitted, extra.file, archive, mode, logTailBytes, smallTextMax);
    }

    private static void addSliceForFile(
        List<Slice> slices,
        Budget budget,
        List<String> omitted,
        File file,
        String archive,
        Mode mode,
        int logTailBytes,
        int smallTextMax
    ) {
        long size;
        try {
            size = file.length();
        } catch (RuntimeException ex) {
            omitted.add(archive + "\tunreadable-size");
            return;
        }
        int cap = mode == Mode.TAIL ? logTailBytes : smallTextMax;
        if (cap < 1) cap = 1;
        long offset = 0L;
        long length = size;
        String note = "full";
        if (size > cap) {
            if (mode == Mode.TAIL) {
                offset = size - cap;
                length = cap;
                note = "tail";
            } else {
                length = cap;
                note = "head";
            }
        }
        boolean redact = isPreferencePath(archive);
        if (redact) note = note + "-redacted";
        long storedGuess = length + (size > cap ? 160 : 0);
        if (!budget.take(storedGuess)) {
            omitted.add(archive + "\tskipped-over-remaining-budget");
            if (budget.files <= 0) noteBudget(omitted, budget);
            return;
        }
        slices.add(new Slice(archive, null, file, offset, length, size, note));
    }

    private static Stored writeSlice(ZipOutputStream zos, Slice slice) throws IOException {
        MessageDigest digest = newSha256();
        ZipEntry entry = new ZipEntry(slice.archivePath);
        if (slice.file != null) entry.setTime(slice.file.lastModified());
        else entry.setTime(System.currentTimeMillis());
        zos.putNextEntry(entry);
        long stored = 0L;
        if (slice.inline != null) {
            byte[] data = slice.inline;
            if (isPreferencePath(slice.archivePath)) {
                data = redactPreferenceText(new String(data, StandardCharsets.UTF_8)).getBytes(StandardCharsets.UTF_8);
            }
            zos.write(data);
            digest.update(data);
            stored = data.length;
        } else {
            boolean partial = slice.offset > 0 || slice.length < slice.originalSize;
            if (partial) {
                byte[] banner = ("[bpp-diagnostic-truncated] originalBytes=" + slice.originalSize
                    + " includedBytes=" + slice.length
                    + " offset=" + slice.offset
                    + " note=" + slice.note + "\n").getBytes(StandardCharsets.UTF_8);
                zos.write(banner);
                digest.update(banner);
                stored += banner.length;
            }
            byte[] data = readRange(slice.file, slice.offset, slice.length);
            if (isPreferencePath(slice.archivePath)) {
                data = redactPreferenceText(new String(data, StandardCharsets.UTF_8)).getBytes(StandardCharsets.UTF_8);
            }
            zos.write(data);
            digest.update(data);
            stored += data.length;
        }
        zos.closeEntry();
        return new Stored(stored, toHex(digest.digest()));
    }

    private static byte[] readRange(File file, long offset, long length) throws IOException {
        if (length > Integer.MAX_VALUE - 8) throw new IOException("range too large");
        int want = (int) Math.max(0L, length);
        byte[] data = new byte[want];
        try (RandomAccessFile raf = new RandomAccessFile(file, "r")) {
            raf.seek(Math.max(0L, offset));
            int off = 0;
            while (off < want) {
                int n = raf.read(data, off, want - off);
                if (n < 0) break;
                off += n;
            }
            if (off == want) return data;
            byte[] trimmed = new byte[off];
            System.arraycopy(data, 0, trimmed, 0, off);
            return trimmed;
        }
    }

    static boolean shouldInclude(String relative, String name) {
        if (name == null || name.length() == 0) return false;
        String lowerName = name.toLowerCase(Locale.ROOT);
        String lowerRel = relative == null ? lowerName : relative.toLowerCase(Locale.ROOT);
        if (isSensitivePath(lowerName) || isSensitivePath(lowerRel)) return false;
        if (isDatabaseName(lowerName)) return false;
        if (isArchiveOrBinary(lowerName) && !lowerName.startsWith("tombstone")) return false;
        if (lowerName.startsWith("tombstone") || lowerRel.contains("tombstone")) return true;
        if (isLogName(lowerName)) return true;
        if ("modpack.json".equals(lowerName) || lowerName.endsWith(".state") || lowerName.endsWith(".cfg")) {
            return true;
        }
        boolean configish = lowerRel.contains("/config/") || lowerRel.startsWith("config/")
            || lowerRel.contains("shared_prefs/") || lowerRel.contains("shared-prefs/")
            || lowerRel.contains("/datastore/") || lowerRel.contains("datastore/")
            || lowerRel.contains("bpp-diagnostics/") || lowerRel.startsWith("bpp-diagnostics/")
            || lowerRel.contains("no_backup/") || lowerRel.contains("no-backup/");
        if (configish && isTextConfig(lowerName)) return true;
        if ((lowerRel.contains("/bepinex/") || lowerRel.startsWith("bepinex/")) && isTextConfig(lowerName)) {
            return true;
        }
        if (hasSegment(lowerRel, "logs") && !isArchiveOrBinary(lowerName)) return true;
        if (lowerRel.contains("persistentdata/") && isTextConfig(lowerName)) return true;
        return false;
    }

    private static boolean includeStateFile(String name) {
        String lowerName = name == null ? "" : name.toLowerCase(Locale.ROOT);
        if (isDatabaseName(lowerName) || isArchiveOrBinary(lowerName) || isSensitivePath(lowerName)) return false;
        return isTextConfig(lowerName) || lowerName.endsWith(".preferences_pb") || lowerName.endsWith(".pb");
    }

    private static boolean hasSegment(String lowerRel, String segment) {
        String padded = "/" + (lowerRel == null ? "" : lowerRel) + "/";
        return padded.contains("/" + segment + "/");
    }

    private static boolean isLogName(String lowerName) {
        return lowerName.endsWith(".log")
            || lowerName.endsWith(".trace")
            || lowerName.endsWith(".stacktrace")
            || lowerName.endsWith(".stderr")
            || lowerName.contains("logcat")
            || lowerName.contains("player.log")
            || lowerName.equals("traces.txt")
            || lowerName.equals("logoutput.log")
            || lowerName.equals("errorlog.log");
    }

    private static boolean isTextConfig(String lowerName) {
        return lowerName.endsWith(".xml")
            || lowerName.endsWith(".json")
            || lowerName.endsWith(".txt")
            || lowerName.endsWith(".properties")
            || lowerName.endsWith(".tsv")
            || lowerName.endsWith(".ini")
            || lowerName.endsWith(".cfg")
            || lowerName.endsWith(".state");
    }

    private static boolean isDatabaseName(String lowerName) {
        return lowerName.endsWith(".db")
            || lowerName.endsWith(".sqlite")
            || lowerName.endsWith(".sqlite3")
            || lowerName.endsWith(".db-wal")
            || lowerName.endsWith(".db-shm")
            || lowerName.endsWith(".sqlite-wal")
            || lowerName.endsWith(".sqlite-shm")
            || lowerName.endsWith(".wal")
            || lowerName.endsWith(".shm");
    }

    private static boolean isArchiveOrBinary(String lowerName) {
        return lowerName.endsWith(".dll")
            || lowerName.endsWith(".so")
            || lowerName.endsWith(".dex")
            || lowerName.endsWith(".apk")
            || lowerName.endsWith(".zip")
            || lowerName.endsWith(".mp4")
            || lowerName.endsWith(".png")
            || lowerName.endsWith(".jpg")
            || lowerName.endsWith(".jpeg")
            || lowerName.endsWith(".webp")
            || lowerName.endsWith(".gif")
            || lowerName.endsWith(".pdb")
            || lowerName.endsWith(".dat")
            || lowerName.endsWith(".bin")
            || lowerName.endsWith(".jar")
            || lowerName.endsWith(".bundle");
    }

    private static Mode modeFor(String relative, String name) {
        String lowerName = name == null ? "" : name.toLowerCase(Locale.ROOT);
        String lowerRel = relative == null ? "" : relative.toLowerCase(Locale.ROOT);
        if (isLogName(lowerName) || lowerName.startsWith("tombstone") || lowerRel.contains("/logs/")) {
            return Mode.TAIL;
        }
        return Mode.HEAD;
    }

    private static boolean isPreferencePath(String archivePath) {
        String lower = archivePath == null ? "" : archivePath.toLowerCase(Locale.ROOT);
        boolean prefs = lower.contains("shared_prefs/")
            || lower.contains("shared-prefs/")
            || lower.contains("/datastore/")
            || lower.contains("datastore/")
            || lower.contains("no_backup/")
            || lower.contains("no-backup/");
        return prefs && (lower.endsWith(".xml") || lower.endsWith(".txt") || lower.endsWith(".properties")
            || lower.endsWith(".cfg") || lower.endsWith(".json"));
    }

    private static void walkTree(
        File root,
        File dir,
        int depth,
        int maxDepth,
        int maxLines,
        int[] lines,
        boolean[] truncated,
        StringBuilder sb
    ) {
        if (truncated[0] || lines[0] >= maxLines) {
            truncated[0] = true;
            return;
        }
        File[] children = dir.listFiles();
        if (children == null) {
            appendTreeLine(sb, lines, "unreadable", dir.lastModified(), safeRelative(root, dir));
            return;
        }
        Arrays.sort(children, new Comparator<File>() {
            @Override
            public int compare(File a, File b) {
                return a.getName().compareTo(b.getName());
            }
        });
        for (int i = 0; i < children.length; i++) {
            if (lines[0] >= maxLines) {
                truncated[0] = true;
                return;
            }
            File child = children[i];
            String relative = safeRelative(root, child);
            if (child.isDirectory()) {
                appendTreeLine(sb, lines, "dir", child.lastModified(), relative + "/");
                boolean shallow = HEAVY_DIRS.contains(child.getName()) || child.getName().startsWith("staging-");
                if (depth + 1 >= maxDepth || shallow) {
                    if (shallow) {
                        File[] nested = child.listFiles();
                        int count = nested == null ? -1 : nested.length;
                        appendTreeLine(sb, lines, "children=" + count, child.lastModified(), relative + "/");
                        if (nested != null && (child.getName().equals("plugins") || child.getName().equals("core"))) {
                            Arrays.sort(nested, new Comparator<File>() {
                                @Override
                                public int compare(File a, File b) {
                                    return a.getName().compareTo(b.getName());
                                }
                            });
                            for (int n = 0; n < nested.length && lines[0] < maxLines; n++) {
                                File leaf = nested[n];
                                appendTreeLine(sb, lines, String.valueOf(leaf.length()), leaf.lastModified(),
                                    safeRelative(root, leaf));
                            }
                        }
                    }
                    continue;
                }
                walkTree(root, child, depth + 1, maxDepth, maxLines, lines, truncated, sb);
            } else {
                appendTreeLine(sb, lines, String.valueOf(child.length()), child.lastModified(), relative);
            }
        }
    }

    private static void appendTreeLine(StringBuilder sb, int[] lines, String size, long mtime, String relative) {
        lines[0]++;
        sb.append(size).append('\t').append(mtime).append('\t').append(relative).append('\n');
    }

    private static long appendLoadSegments(
        StringBuilder sb,
        ByteBuffer buf,
        int dataLength,
        long phoff,
        int phentsize,
        int phnum,
        boolean elf64
    ) {
        long minAlign = Long.MAX_VALUE;
        if (phentsize <= 0 || phnum < 0 || phnum > 64 || phoff < 0) {
            sb.append("phdrInvalid\n");
            return minAlign;
        }
        for (int i = 0; i < phnum; i++) {
            long offLong = phoff + (long) i * phentsize;
            if (offLong < 0 || offLong + (elf64 ? 56 : 32) > dataLength) {
                sb.append("phdrTruncated index=").append(i).append('\n');
                break;
            }
            int off = (int) offLong;
            int type = buf.getInt(off);
            if (type != 1) continue;
            long offset;
            long vaddr;
            long filesz;
            long memsz;
            long align;
            if (elf64) {
                offset = buf.getLong(off + 8);
                vaddr = buf.getLong(off + 16);
                filesz = buf.getLong(off + 32);
                memsz = buf.getLong(off + 40);
                align = buf.getLong(off + 48);
            } else {
                offset = buf.getInt(off + 4) & 0xffffffffL;
                vaddr = buf.getInt(off + 8) & 0xffffffffL;
                filesz = buf.getInt(off + 16) & 0xffffffffL;
                memsz = buf.getInt(off + 20) & 0xffffffffL;
                align = buf.getInt(off + 28) & 0xffffffffL;
            }
            sb.append("PT_LOAD offset=").append(offset)
                .append(" vaddr=").append(vaddr)
                .append(" filesz=").append(filesz)
                .append(" memsz=").append(memsz)
                .append(" align=").append(align).append('\n');
            if (align > 0 && align < minAlign) minAlign = align;
        }
        return minAlign;
    }

    private static String renderManifest(
        List<String> included,
        List<String> unavailable,
        List<String> omitted,
        long storedBytes
    ) {
        StringBuilder sb = new StringBuilder();
        sb.append("# BPP Android diagnostic manifest\n");
        sb.append("# revision\t").append(EXPORTER_REVISION).append('\n');
        sb.append("# generated\t").append(nowIso()).append('\n');
        sb.append("# storedBytes\t").append(storedBytes).append('\n');
        sb.append("# included columns: archivePath originalBytes storedBytes sha256 note\n");
        sb.append("\n[included]\n");
        appendLines(sb, included);
        sb.append("\n[unavailable]\n");
        appendLines(sb, unavailable);
        sb.append("\n[omitted]\n");
        appendLines(sb, omitted);
        sb.append('\n');
        return sb.toString();
    }

    private static void appendLines(StringBuilder sb, List<String> lines) {
        if (lines.isEmpty()) {
            sb.append("(none)\n");
            return;
        }
        for (int i = 0; i < lines.size(); i++) sb.append(lines.get(i)).append('\n');
    }

    private static void putBytes(ZipOutputStream zos, String name, byte[] data, long time) throws IOException {
        ZipEntry entry = new ZipEntry(name);
        entry.setTime(time);
        zos.putNextEntry(entry);
        zos.write(data);
        zos.closeEntry();
    }

    private static String uniquePath(Set<String> used, String desired) {
        String clean = sanitizeArchivePath(desired);
        if (clean == null) return null;
        if (used.add(clean)) return clean;
        int dot = clean.lastIndexOf('.');
        int slash = clean.lastIndexOf('/');
        if (dot < slash) dot = -1;
        String base = dot > 0 ? clean.substring(0, dot) : clean;
        String ext = dot > 0 ? clean.substring(dot) : "";
        for (int i = 2; i < 1000; i++) {
            String candidate = base + "-" + i + ext;
            if (used.add(candidate)) return candidate;
        }
        return null;
    }

    private static boolean isSymlinkEscape(File root, File child) {
        try {
            if (!Files.isSymbolicLink(child.toPath())) return false;
            String rootPath = root.getCanonicalPath();
            String childPath = child.getCanonicalPath();
            String prefix = rootPath.endsWith(File.separator) ? rootPath : rootPath + File.separator;
            return !childPath.equals(rootPath) && !childPath.startsWith(prefix);
        } catch (IOException ex) {
            return true;
        }
    }

    private static String safeRelative(File root, File file) {
        try {
            String rootPath = root.getAbsolutePath();
            String filePath = file.getAbsolutePath();
            if (filePath.equals(rootPath)) return ".";
            String prefix = rootPath.endsWith(File.separator) ? rootPath : rootPath + File.separator;
            if (filePath.startsWith(prefix)) {
                return filePath.substring(prefix.length()).replace('\\', '/');
            }
        } catch (RuntimeException ignored) {
        }
        return file.getName();
    }

    private static String canonical(File file) {
        try {
            return file.getCanonicalPath();
        } catch (IOException ex) {
            return null;
        }
    }

    private static void noteBudget(List<String> omitted, Budget budget) {
        budget.exhausted = true;
        if (!budget.exhaustedNoted) {
            budget.exhaustedNoted = true;
            omitted.add("summary\tstored-budget-reached");
        }
    }

    private static String oneLine(String value) {
        if (value == null) return "";
        return value.replace('\n', ' ').replace('\r', ' ');
    }

    private static String safe(String value) {
        return value == null || value.length() == 0 ? "未知" : value;
    }

    private static MessageDigest newSha256() throws IOException {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (Exception ex) {
            throw new IOException("SHA-256 unavailable", ex);
        }
    }

    private static String toHex(byte[] data) {
        char[] hex = "0123456789abcdef".toCharArray();
        char[] out = new char[data.length * 2];
        for (int i = 0; i < data.length; i++) {
            int v = data[i] & 0xff;
            out[i * 2] = hex[v >>> 4];
            out[i * 2 + 1] = hex[v & 0x0f];
        }
        return new String(out);
    }

    public static byte[] readPrefix(File file, int maxBytes) throws IOException {
        int want = Math.max(0, maxBytes);
        byte[] data = new byte[Math.min(want, ELF_PROBE_BYTES)];
        try (InputStream in = new java.io.FileInputStream(file)) {
            int off = 0;
            while (off < data.length) {
                int n = in.read(data, off, data.length - off);
                if (n < 0) break;
                off += n;
            }
            if (off == data.length) return data;
            byte[] trimmed = new byte[off];
            System.arraycopy(data, 0, trimmed, 0, off);
            return trimmed;
        }
    }

    private enum Mode {
        FULL, TAIL, HEAD
    }

    private static final class Budget {
        long bytes;
        int files;
        boolean exhausted;
        boolean exhaustedNoted;

        Budget(long bytes, int files) {
            this.bytes = bytes;
            this.files = files;
        }

        boolean take(long size) {
            if (files <= 0 || size < 0) {
                exhausted = true;
                return false;
            }
            if (size > bytes) return false;
            bytes -= size;
            files -= 1;
            if (files <= 0) exhausted = true;
            return true;
        }
    }

    private static final class Slice {
        final String archivePath;
        final byte[] inline;
        final File file;
        final long offset;
        final long length;
        final long originalSize;
        final String note;

        Slice(String archivePath, byte[] inline, File file, long offset, long length, long originalSize, String note) {
            this.archivePath = archivePath;
            this.inline = inline;
            this.file = file;
            this.offset = offset;
            this.length = length;
            this.originalSize = originalSize;
            this.note = note;
        }
    }

    private static final class Stored {
        final long storedBytes;
        final String sha256;

        Stored(long storedBytes, String sha256) {
            this.storedBytes = storedBytes;
            this.sha256 = sha256;
        }
    }

}
