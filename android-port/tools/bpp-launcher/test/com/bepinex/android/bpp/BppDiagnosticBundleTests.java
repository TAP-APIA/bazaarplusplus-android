package com.bepinex.android.bpp;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

public final class BppDiagnosticBundleTests {

    public static void main(String[] args) throws Exception {
        testIncludesLogsConfigsAndOmitsBinariesDatabasesSecrets();
        testTailsLargeLogsAndRejectsTraversal();
        testRedactsPreferenceSecretsFromStateTree();
        testElfAlignment();
        testLogcatCommandsDoNotClear();
        testAppendCappedAndCaptureProcess();
        testTreeListsPlugins();
        testReadmeExplainsTheZip();
        testGenerationStageAndLoggingConfig();
        testExitReasonNamesAndDirectoryListing();
        testPreferenceProtoKeys();
        System.out.println("PASS: all BppDiagnosticBundle tests succeeded.");
    }

    private static void testIncludesLogsConfigsAndOmitsBinariesDatabasesSecrets() throws Exception {
        File root = Files.createTempDirectory("bpp-diag-scan").toFile();
        File game = new File(root, "game");
        write(new File(game, "BepInEx/LogOutput.log"), "plugin.initialization.succeeded\n");
        write(new File(game, "BepInEx/bpp-startup-trace.log"), "trace\n");
        write(new File(game, "BepInEx/config/BazaarPlusPlus.cfg"), "[OverlayUI]\nScalePercent = 100\n");
        write(new File(game, "modpacks/bpp/modpack.json"), "{\"name\":\"bpp\"}\n");
        write(new File(game, "modpacks/bpp/logs/mod.log"), "mod-log\n");
        write(new File(game, "PersistentData/tombstone_01"), "DEBUG tombstone\n");
        write(new File(game, "Player.log"), "Unity Player\n");
        write(new File(game, "notes.txt"), "not a log\n");
        write(new File(game, "BepInEx/plugins/BazaarPlusPlus.dll"), "MZ-not-really");
        write(new File(game, "BazaarPlusPlusV6/history.db"), "sqlite-bytes");
        write(new File(game, "secrets/release.keystore"), "do-not-pack");
        write(new File(game, "BepInEx/config/my-password.txt"), "hidden");
        File outside = new File(root, "outside.txt");
        write(outside, "escaped");
        Files.createSymbolicLink(new File(game, "escape.log").toPath(), outside.toPath());

        File zip = new File(root, "out.zip");
        BppDiagnosticBundle.Request request = new BppDiagnosticBundle.Request(
            "readme-body",
            Arrays.asList(new BppDiagnosticBundle.TextEntry("device/build.txt", "model=test")),
            Arrays.asList(new BppDiagnosticBundle.ScanRoot(game, "collected/game-root")),
            Arrays.asList(new BppDiagnosticBundle.ExtraFile(new File(game, "missing.log"), "logcat/missing.log", true))
        );
        BppDiagnosticBundle.Result result = BppDiagnosticBundle.write(zip, request, 1024, 1024, 1024 * 1024, 100);
        assertTrue(result.includedFiles > 0, "expected included files");
        String log = entry(zip, "collected/game-root/BepInEx/LogOutput.log");
        assertTrue(log.contains("plugin.initialization.succeeded"), "LogOutput missing");
        assertTrue(entry(zip, "collected/game-root/BepInEx/config/BazaarPlusPlus.cfg").contains("ScalePercent"), "cfg missing");
        assertTrue(entry(zip, "collected/game-root/modpacks/bpp/modpack.json").contains("bpp"), "modpack.json missing");
        assertTrue(entry(zip, "collected/game-root/PersistentData/tombstone_01").contains("tombstone"), "tombstone missing");
        assertTrue(entry(zip, "collected/game-root/Player.log").contains("Unity Player"), "Player.log missing");
        assertTrue(entry(zip, "device/build.txt").contains("model=test"), "inline text missing");
        assertMissing(zip, "collected/game-root/notes.txt");
        assertMissing(zip, "collected/game-root/BepInEx/plugins/BazaarPlusPlus.dll");
        assertMissing(zip, "collected/game-root/BazaarPlusPlusV6/history.db");
        assertMissing(zip, "collected/game-root/secrets/release.keystore");
        assertMissing(zip, "collected/game-root/BepInEx/config/my-password.txt");
        assertMissing(zip, "collected/game-root/escape.log");
        String manifest = entry(zip, "manifest.txt");
        assertTrue(manifest.contains("[unavailable]"), "manifest unavailable section missing");
        assertTrue(manifest.contains("logcat/missing.log"), "missing extra not recorded");
        assertTrue(manifest.contains("database"), "database omit not recorded");
        assertTrue(manifest.contains("sensitive-name"), "keystore omit not recorded");
        assertTrue(manifest.contains("symlink-escape"), "symlink escape not recorded");
        assertTrue(manifest.contains("skipped-heavy-dir"), "plugins dir should be listed as heavy");
        assertTrue(entry(zip, "README.txt").equals("readme-body"), "readme was replaced");
    }

    private static void testTailsLargeLogsAndRejectsTraversal() throws Exception {
        File root = Files.createTempDirectory("bpp-diag-tail").toFile();
        File log = new File(root, "LogOutput.log");
        byte[] body = new byte[300];
        java.util.Arrays.fill(body, (byte) 'x');
        System.arraycopy("HEAD".getBytes(StandardCharsets.UTF_8), 0, body, 0, 4);
        byte[] marker = "TAILMARKER".getBytes(StandardCharsets.UTF_8);
        System.arraycopy(marker, 0, body, body.length - marker.length, marker.length);
        Files.write(log.toPath(), body);
        File zip = new File(root, "out.zip");
        BppDiagnosticBundle.write(
            zip,
            new BppDiagnosticBundle.Request(
                "r",
                Arrays.asList(new BppDiagnosticBundle.TextEntry("../evil.txt", "nope")),
                Arrays.<BppDiagnosticBundle.ScanRoot>asList(),
                Arrays.asList(new BppDiagnosticBundle.ExtraFile(log, "logs/LogOutput.log", true))
            ),
            64,
            32,
            1024 * 1024,
            20
        );
        String stored = entry(zip, "logs/LogOutput.log");
        assertTrue(stored.contains("bpp-diagnostic-truncated"), "tail banner missing");
        assertTrue(stored.contains("originalBytes=300"), "original size missing");
        assertTrue(stored.contains("TAILMARKER"), "tail dropped the end of the log");
        assertTrue(!stored.contains("HEAD"), "tail kept the start of the log");
        String manifest = entry(zip, "manifest.txt");
        assertTrue(manifest.contains("../evil.txt") || manifest.contains("rejected-archive-path"), manifest);
        assertMissing(zip, "../evil.txt");
        assertMissing(zip, "evil.txt");
    }

    private static void testRedactsPreferenceSecretsFromStateTree() throws Exception {
        File root = Files.createTempDirectory("bpp-diag-prefs").toFile();
        File prefs = new File(root, "shared_prefs");
        write(new File(prefs, "secure.xml"),
            "<map><string name=\"api_token\">super-secret</string><string name=\"theme\">dark</string></map>");
        write(new File(prefs, "settings.preferences_pb"), "binary-token-remains");
        File zip = new File(root, "out.zip");
        BppDiagnosticBundle.write(zip, new BppDiagnosticBundle.Request(
            "r",
            Arrays.<BppDiagnosticBundle.TextEntry>asList(),
            Arrays.asList(new BppDiagnosticBundle.ScanRoot(prefs, "state/shared-prefs", true)),
            Arrays.<BppDiagnosticBundle.ExtraFile>asList()
        ));
        String xml = entry(zip, "state/shared-prefs/secure.xml");
        assertTrue(xml.contains("[redacted]"), xml);
        assertTrue(!xml.contains("super-secret"), xml);
        assertTrue(xml.contains("dark"), "non-secret preference was removed");
        String pb = entry(zip, "state/shared-prefs/settings.preferences_pb");
        assertTrue(pb.contains("binary-token-remains"), "binary datastore should be copied unchanged");
        String redacted = BppDiagnosticBundle.redactPreferenceText("password=hunter2\ntheme=dark\n");
        assertTrue(redacted.contains("password=[redacted]"), redacted);
        assertTrue(redacted.contains("theme=dark"), redacted);
    }

    private static void testElfAlignment() {
        byte[] elf = new byte[128];
        elf[0] = 0x7f;
        elf[1] = 'E';
        elf[2] = 'L';
        elf[3] = 'F';
        elf[4] = 2;
        elf[5] = 1;
        ByteBuffer buf = ByteBuffer.wrap(elf).order(ByteOrder.LITTLE_ENDIAN);
        buf.putLong(32, 64L);
        buf.putShort(54, (short) 56);
        buf.putShort(56, (short) 1);
        buf.putInt(64, 1);
        buf.putLong(64 + 48, 16384L);
        String described = BppDiagnosticBundle.describeElfLoadSegments(elf, "libil2cpp.so");
        assertTrue(described.contains("PT_LOAD"), described);
        assertTrue(described.contains("align=16384"), described);
        assertTrue(described.contains("minLoadAlign=16384"), described);
        assertTrue(BppDiagnosticBundle.describeElfLoadSegments(new byte[] {1, 2, 3}, "x").contains("unrecognized"),
            "non-elf should be explicit");
    }

    private static void testLogcatCommandsDoNotClear() {
        List<String> rolling = BppDiagnosticBundle.rollingLogcatCommand(new File("/data/bpp-logcat"));
        assertTrue(rolling.contains("-d"), "rolling logcat must exit so stdio flushes");
        assertTrue(!rolling.contains("-f"), "logcat -f stops at an 8KB stdio buffer");
        assertTrue(rolling.contains("crash"), "crash buffer should be included");
        assertTrue(!BppDiagnosticBundle.commandClearsLogcat(rolling), "rolling command clears the buffer");
        List<String> filtered = BppDiagnosticBundle.rollingLogcatCommand(22781);
        assertTrue(filtered.contains("--pid=22781"), filtered.toString());
        assertTrue("line-c\n".equals(BppDiagnosticBundle.suffixAfterMarker("line-a\nline-b\nline-c\n", "line-b")),
            "suffix after marker");
        assertTrue(BppDiagnosticBundle.suffixAfterMarker("other\n", "line-b").startsWith("[buffer-rotated]"),
            "rotated buffer should be kept");
        List<String> dump = BppDiagnosticBundle.dumpLogcatCommand("main");
        assertTrue(dump.contains("-d"), "dump must be non-blocking");
        assertTrue(!BppDiagnosticBundle.commandClearsLogcat(dump), "dump command clears the buffer");
        assertTrue(BppDiagnosticBundle.commandClearsLogcat(Arrays.asList("logcat", "-c")), "detector missed -c");
        boolean threw = false;
        try {
            BppDiagnosticBundle.captureProcess(Arrays.asList("logcat", "-c"), 1000L, 100);
        } catch (Exception ex) {
            threw = true;
        }
        assertTrue(threw, "captureProcess must refuse logcat -c");
    }

    private static void testAppendCappedAndCaptureProcess() throws Exception {
        File root = Files.createTempDirectory("bpp-diag-append").toFile();
        File note = new File(root, "launch-attempts.log");
        BppDiagnosticBundle.appendCapped(note, "one", 32);
        BppDiagnosticBundle.appendCapped(note, "two", 32);
        BppDiagnosticBundle.appendCapped(note, "three-is-longer", 32);
        String text = Files.readString(note.toPath());
        assertTrue(text.length() <= 32, "cap exceeded: " + text.length());
        assertTrue(text.contains("three-is-longer"), text);
        String echo = BppDiagnosticBundle.captureProcessText(Arrays.asList("echo", "hello-diag"), 5000L, 100);
        assertTrue(echo.contains("hello-diag"), echo);
        assertTrue(BppDiagnosticBundle.sanitizeArchivePath("../etc/passwd") == null, "traversal accepted");
        assertTrue(BppDiagnosticBundle.sanitizeArchivePath("logs/LogOutput.log").equals("logs/LogOutput.log"), "normal path rejected");
        assertTrue(BppDiagnosticBundle.isSensitivePath("dir/release.keystore"), "keystore not sensitive");
        assertTrue(!BppDiagnosticBundle.isSensitivePath("BepInEx/LogOutput.log"), "LogOutput was treated as secret");
    }

    private static void testTreeListsPlugins() throws Exception {
        File root = Files.createTempDirectory("bpp-diag-tree").toFile();
        write(new File(root, "BepInEx/plugins/BazaarPlusPlus.dll"), "dll");
        write(new File(root, "BepInEx/LogOutput.log"), "log");
        String tree = BppDiagnosticBundle.renderTree(root, 50, 6);
        assertTrue(tree.contains("BazaarPlusPlus.dll"), tree);
        assertTrue(tree.contains("LogOutput.log"), tree);
        assertTrue(!tree.contains("\ndll\n") && !tree.contains("\tlog\n"), "tree copied file bytes");
    }

    private static void testReadmeExplainsTheZip() {
        String readme = BppDiagnosticBundle.readme("2026-10-27T00:00:00Z", "6.0.0-preview.39", "com.bepinex.android");
        String[] required = new String[] {
            "manifest.txt", "device/", "packages/", "deploy/", "launch/", "logcat/", "crashes/", "state/",
            "LogOutput", "libil2cpp", "灰屏", "下载", "keystore", "getHistoricalProcessExitReasons",
            "game-heartbeat"
        };
        for (int i = 0; i < required.length; i++) {
            assertTrue(readme.contains(required[i]), "readme missing " + required[i]);
        }
    }

    private static void testGenerationStageAndLoggingConfig() throws Exception {
        BppDiagnosticBundle.GenerationProbe probe = new BppDiagnosticBundle.GenerationProbe();
        assertTrue("starting".equals(BppDiagnosticBundle.generationStageId(probe)), "empty probe");
        probe.unityZipBytes = 1000;
        assertTrue("unity-libs".equals(BppDiagnosticBundle.generationStageId(probe)), "zip only");
        probe.unityLibFiles = 40;
        assertTrue("cpp2il".equals(BppDiagnosticBundle.generationStageId(probe)), "libs without interop");
        assertTrue(BppDiagnosticBundle.generationPercent(probe, 0) < 0, "cpp2il percent must stay unknown");
        assertTrue(BppDiagnosticBundle.generationStageLabel("cpp2il").contains("Cpp2IL"), "stage label");
        probe.interopFiles = 12;
        probe.interopBytes = 4096;
        assertTrue("interop".equals(BppDiagnosticBundle.generationStageId(probe)), "writing interop");
        probe.interopFilesAtStart = 12;
        probe.logIsNew = true;
        probe.logOutputBytes = 80;
        assertTrue("plugins".equals(BppDiagnosticBundle.generationStageId(probe)), "plugins after a fresh log");
        String cfg = "[Logging]\nUnityLogListening = false\n\n[Logging.Disk]\nWriteUnityLog = false\nInstantFlushing = false\n";
        String enabled = BppDiagnosticBundle.enableDiagnosticLogging(cfg);
        assertTrue(enabled.contains("UnityLogListening = true"), enabled);
        assertTrue(enabled.contains("WriteUnityLog = true"), enabled);
        assertTrue(enabled.contains("InstantFlushing = true"), enabled);
        assertTrue(enabled.equals(BppDiagnosticBundle.enableDiagnosticLogging(enabled)), "patch not idempotent");
        File root = Files.createTempDirectory("bpp-diag-logs").toFile();
        write(new File(root, "BepInEx/logs/LogOutput.log"), "log\n");
        write(new File(root, "BepInEx/plugins/nested.log"), "inside-plugin\n");
        write(new File(root, "BepInEx/plugins/skip.dll"), "dll");
        List<File> logs = BppDiagnosticBundle.findLogFiles(root, 20);
        boolean sawNested = false;
        for (int i = 0; i < logs.size(); i++) {
            if (logs.get(i).getName().equals("nested.log")) sawNested = true;
        }
        assertTrue(sawNested, "log inside plugins was skipped");
        String empty = BppDiagnosticBundle.describeDirectory(new File(root, "BepInEx/interop"), 10);
        assertTrue(empty.contains("exists=false"), empty);
        File logsDir = new File(root, "BepInEx/logs");
        String listed = BppDiagnosticBundle.describeDirectory(logsDir, 10);
        assertTrue(listed.contains("LogOutput.log"), listed);
        assertTrue(listed.contains("children=1"), listed);
    }

    private static void testExitReasonNamesAndDirectoryListing() {
        assertTrue("LOW_MEMORY".equals(BppDiagnosticBundle.exitReasonName(3)), "reason 3");
        assertTrue("CACHED".equals(BppDiagnosticBundle.importanceName(400)), "importance 400");
        assertTrue(BppDiagnosticBundle.exitSubReasonName(6).equals("MEMORY_PRESSURE"), "subreason");
        assertTrue(BppDiagnosticBundle.exitSubReasonName(-1).equals("unavailable"), "missing subreason");
        assertTrue(BppDiagnosticBundle.FIRST_RUN_OVERLAY_TEXT.contains("不要切走"), "overlay copy");
    }

    private static void testPreferenceProtoKeys() {
        byte[] value = protoBytes(new byte[][] {
            new byte[] {0x2a, 0x04}, "dark".getBytes(StandardCharsets.UTF_8)
        });
        byte[] entry = protoBytes(new byte[][] {
            new byte[] {0x0a, 0x05}, "theme".getBytes(StandardCharsets.UTF_8),
            new byte[] {0x12, (byte) value.length}, value
        });
        byte[] tokenValue = protoBytes(new byte[][] { new byte[] {0x08, 0x01} });
        byte[] tokenEntry = protoBytes(new byte[][] {
            new byte[] {0x0a, 0x09}, "api_token".getBytes(StandardCharsets.UTF_8),
            new byte[] {0x12, (byte) tokenValue.length}, tokenValue
        });
        byte[] message = protoBytes(new byte[][] {
            new byte[] {0x0a, (byte) entry.length}, entry,
            new byte[] {0x0a, (byte) tokenEntry.length}, tokenEntry
        });
        String decoded = BppDiagnosticBundle.decodePreferenceProto(message);
        assertTrue(decoded.contains("theme=dark"), decoded);
        assertTrue(decoded.contains("api_token=[redacted]"), decoded);
        assertTrue(!decoded.contains("true"), decoded);
    }

    private static byte[] protoBytes(byte[][] parts) {
        int size = 0;
        for (int i = 0; i < parts.length; i++) size += parts[i].length;
        byte[] out = new byte[size];
        int at = 0;
        for (int i = 0; i < parts.length; i++) {
            System.arraycopy(parts[i], 0, out, at, parts[i].length);
            at += parts[i].length;
        }
        return out;
    }

    private static void write(File file, String text) throws Exception {
        File parent = file.getParentFile();
        if (parent != null) parent.mkdirs();
        Files.write(file.toPath(), text.getBytes(StandardCharsets.UTF_8));
    }

    private static String entry(File zip, String name) throws Exception {
        try (ZipFile zf = new ZipFile(zip)) {
            ZipEntry entry = zf.getEntry(name);
            assertTrue(entry != null, "missing zip entry " + name);
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            try (java.io.InputStream in = zf.getInputStream(entry)) {
                int n;
                while ((n = in.read(buf)) != -1) bos.write(buf, 0, n);
            }
            return bos.toString(StandardCharsets.UTF_8);
        }
    }

    private static void assertMissing(File zip, String name) throws Exception {
        try (ZipFile zf = new ZipFile(zip)) {
            assertTrue(zf.getEntry(name) == null, "unexpected zip entry " + name);
        }
    }

    private static void assertTrue(boolean value, String message) {
        if (!value) throw new AssertionError(message);
    }
}
