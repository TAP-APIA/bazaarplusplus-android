"""Execute production window policy with queued lifecycle fakes, not a device/layout emulator."""
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest


STUBS = {
    "android/R.java": "package android; public class R { public static class id { public static int content=1; } }",
    "android/os/Build.java": "package android.os; public class Build { public static class VERSION { public static int SDK_INT=35; } }",
    "android/os/Looper.java": "package android.os; public class Looper { public static Looper getMainLooper(){return new Looper();} }",
    "android/os/SystemClock.java": "package android.os; public class SystemClock { public static long uptimeMillis(){return 0;} }",
    "android/os/Handler.java": """package android.os; import java.util.*; public class Handler {
        static class Work {Runnable run; Object token; Work(Runnable r,Object t){run=r;token=t;}}
        static final List<Work> queue=new ArrayList<>();
        public Handler(Looper l){} public boolean postAtTime(Runnable r,Object t,long n){queue.add(new Work(r,t));return true;}
        public boolean post(Runnable r){return postAtTime(r,null,0);}
        public void removeCallbacksAndMessages(Object t){queue.removeIf(w -> t==null || w.token==t);}
        public static void drain(){while(!queue.isEmpty()) queue.remove(0).run.run();}
    }""",
    "android/util/Log.java": "package android.util; public class Log { public static int i(String t,String m){return 0;} public static int e(String t,String m,Throwable e){return 0;} }",
    "android/app/Activity.java": """package android.app; public class Activity {
        public final android.view.Window window=new android.view.Window();
        public boolean focused=true,finishing,destroyed;
        public boolean isFinishing(){return finishing;} public boolean isDestroyed(){return destroyed;}
        public boolean hasWindowFocus(){return focused;}
        public android.view.Window getWindow(){return window;}
    }""",
    "android/view/ViewTreeObserver.java": """package android.view; import java.util.*;
    public class ViewTreeObserver {
        public interface OnWindowFocusChangeListener {void onWindowFocusChanged(boolean focus);}
        public final List<OnWindowFocusChangeListener> listeners=new ArrayList<>();
        public boolean isAlive(){return true;}
        public void addOnWindowFocusChangeListener(OnWindowFocusChangeListener l){listeners.add(l);}
        public void removeOnWindowFocusChangeListener(OnWindowFocusChangeListener l){listeners.remove(l);}
        public void focus(boolean value){for(var l:new ArrayList<>(listeners)) l.onWindowFocusChanged(value);}
    }""",
    "android/view/View.java": """package android.view; public class View {
        public static final int SYSTEM_UI_FLAG_LAYOUT_STABLE=256,SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN=1024,
            SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION=512,SYSTEM_UI_FLAG_FULLSCREEN=4,
            SYSTEM_UI_FLAG_HIDE_NAVIGATION=2,SYSTEM_UI_FLAG_IMMERSIVE_STICKY=4096;
        public boolean attached=true,fits=true; public int visibilityCalls,insetRequests,visibility;
        public View content;
        public interface OnApplyWindowInsetsListener { WindowInsets onApplyWindowInsets(View view, WindowInsets insets); }
        public OnApplyWindowInsetsListener insetListener;
        public void setOnApplyWindowInsetsListener(OnApplyWindowInsetsListener l){insetListener=l;}
        public final ViewTreeObserver observer=new ViewTreeObserver();
        public final WindowInsetsController controller=new WindowInsetsController();
        public boolean isAttachedToWindow(){return attached;}
        public ViewTreeObserver getViewTreeObserver(){return observer;}
        public void setFitsSystemWindows(boolean b){fits=b;}
        public View findViewById(int id){return content;}
        public int getSystemUiVisibility(){return visibility;}
        public void setSystemUiVisibility(int flags){visibilityCalls++;visibility=flags;}
        public void requestApplyInsets(){insetRequests++;}
        public WindowInsetsController getWindowInsetsController(){return controller;}
    }""",
    "android/view/WindowInsets.java": """package android.view; public class WindowInsets {
        public static class Type { public static int systemBars(){return 7;} }
    }""",
    "android/view/WindowInsetsController.java": """package android.view; public class WindowInsetsController {
        public static final int BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE=2;
        public int hideCalls,behavior; public boolean transientVisible;
        public void setSystemBarsBehavior(int b){behavior=b;}
        public void hide(int types){hideCalls++;transientVisible=false;}
    }""",
    "android/view/WindowManager.java": """package android.view; public interface WindowManager {
        public static class LayoutParams {
            public static final int FLAG_FULLSCREEN=1024,LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS=3,
                LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES=1;
            public int layoutInDisplayCutoutMode,fitTypes=3;
            public int getFitInsetsTypes(){return fitTypes;} public void setFitInsetsTypes(int t){fitTypes=t;}
        }
    }""",
    "android/view/Window.java": """package android.view; public class Window {
        public final View decor=new View(); public final WindowManager.LayoutParams attributes=new WindowManager.LayoutParams();
        public boolean fits=true; public int attributeWrites;
        public Window(){decor.content=new View();}
        public View getDecorView(){return decor;} public WindowManager.LayoutParams getAttributes(){return attributes;}
        public void setAttributes(WindowManager.LayoutParams a){attributeWrites++;}
        public void setDecorFitsSystemWindows(boolean value){fits=value;} public void addFlags(int flags){}
    }""",
    "com/bepinex/android/bpp/BppLauncherActivity.java": "package com.bepinex.android.bpp; public class BppLauncherActivity extends android.app.Activity {}",
    "GameWindowTests.java": """import android.app.Activity;
    import com.bepinex.android.bpp.BppGameWindow;
    public class GameWindowTests {
        static void check(boolean value,String message){if(!value) throw new AssertionError(message);}
        static void apply(Activity activity){check(BppGameWindow.apply(activity),"Apply not queued");android.os.Handler.drain();}
        static void reset(){BppGameWindow.reset();android.os.Handler.drain();}
        static class LauncherSubclass extends com.bepinex.android.bpp.BppLauncherActivity {}
        public static void main(String[] args){
            Activity game=new Activity(); var w=game.window; var d=w.decor;
            BppGameWindow.apply(game);
            check(d.controller.hideCalls==0,"Window mutated before UI queue dispatch");
            android.os.Handler.drain();
            check(!w.fits && w.attributes.fitTypes==0,"Window must ignore visible bar insets");
            check(d.controller.behavior==2 && d.controller.hideCalls==1,"Initial sticky immersion");
            int immersive=d.visibility;
            check(d.insetListener==null && d.content.insetListener==null,"Window helper must not replace native inset listeners");
            check(d.insetRequests==0 && d.content.insetRequests==0,"Binding must not force inset dispatch");
            int writes=w.attributeWrites;
            d.controller.transientVisible=true;
            for(int i=0;i<20;i++) apply(game);
            check(d.controller.transientVisible && d.controller.hideCalls==1,"Heartbeat fights user's swipe");
            check(w.attributeWrites==writes && d.insetRequests==0 && d.visibilityCalls==1,"Heartbeat forces relayout");
            check(d.content.insetRequests==0,"Heartbeat redispatched child insets");
            game.focused=false; d.observer.focus(false); d.visibility=0;
            apply(game); check(d.controller.hideCalls==1,"Unfocused policy repair stole shade");
            game.focused=true; d.observer.focus(true);
            check(d.controller.hideCalls==1,"Focus repair ran inside Unity dispatch");
            // Unity's callback in the same dispatch restores decor fitting and requests bars,
            // but can leave every legacy immersive bit set (the observed v33 failure).
            w.fits=true; d.visibility=immersive; d.controller.transientVisible=true;
            android.os.Handler.drain();
            check(!w.fits && !d.controller.transientVisible && d.controller.hideCalls==2,"Deferred focus repair missed controller-only reset");
            check(d.insetRequests==0,"Focus return redispatched insets");
            d.visibility=0; // Unity rewrites requested flags after its player finishes loading.
            apply(game);
            check(d.controller.hideCalls==3 && d.visibilityCalls==2,"Unity policy reset not repaired");
            apply(game);
            check(d.controller.hideCalls==3 && d.visibilityCalls==2,"Stable repaired policy reapplied");
            check(d.observer.listeners.size()==1,"Duplicate focus listeners");
            // Obsolete queued focus work cannot hide bars in a replaced Activity.
            BppGameWindow.apply(new Activity()); d.observer.focus(true); android.os.Handler.drain();
            check(d.controller.hideCalls==3,"Replaced binding executed stale focus repair");
            Activity replacement=new Activity(); apply(replacement);
            check(d.observer.listeners.isEmpty(),"Old activity retained its listener");
            check(d.insetListener==null,"Old decor retained inset listener");
            replacement.window.decor.observer.focus(true);
            reset(); check(replacement.window.decor.observer.listeners.isEmpty(),"Reset leaked listener");
            check(replacement.window.decor.insetListener==null,"Reset leaked root insets listener");
            Activity launcher=new com.bepinex.android.bpp.BppLauncherActivity(); apply(launcher);
            Activity subclass=new LauncherSubclass(); apply(subclass);
            check(launcher.window.decor.controller.hideCalls==0 && subclass.window.decor.controller.hideCalls==0
                    && launcher.window.fits && subclass.window.fits,"Launcher window was modified");
            check(replacement.window.decor.controller.hideCalls==1,"Launcher changed game window");
            Activity cancelled=new Activity(); BppGameWindow.apply(cancelled); reset();
            check(cancelled.window.decor.controller.hideCalls==0,"Reset did not cancel queued Activity capture");
            Activity destroyed=new Activity(); BppGameWindow.apply(destroyed); destroyed.destroyed=true; android.os.Handler.drain();
            check(destroyed.window.decor.controller.hideCalls==0,"Queued apply modified destroyed Activity");
            Activity pending=new Activity(); pending.window.decor.attached=false; apply(pending);
            check(pending.window.decor.controller.hideCalls==0,"Unattached decor mutated");
            pending.window.decor.attached=true; apply(pending);
            check(pending.window.decor.controller.hideCalls==1,"Attach retry lost");
            pending.window.decor.observer.focus(true); pending.focused=false; android.os.Handler.drain();
            check(pending.window.decor.controller.hideCalls==1,"Queued focus restore ignored subsequent focus loss");
            reset(); android.os.Build.VERSION.SDK_INT=28;
            Activity legacy=new Activity(); apply(legacy); apply(legacy);
            check(legacy.window.decor.visibilityCalls==1,"Legacy immersive repeats on heartbeat");
            legacy.window.decor.observer.focus(true);
            android.os.Handler.drain();
            check(legacy.window.decor.visibilityCalls==2,"Legacy focus restore missing");
            check(legacy.window.decor.insetListener==null,"Legacy device received modern inset adapter");
            reset(); System.out.println("Game window lifecycle/gesture tests passed");
        }
    }""",
}


class GameWindowTests(unittest.TestCase):
    def test_unity_metadata_survives_fused_host_package(self):
        from patch_manifest import (
            UNITY_WINDOW_METADATA, _encode_utf16_pool_string, _parse_string_pool,
            _get_start_element_info, patch_unity_window_metadata,
        )

        # A binary fixture with an existing false Unity setting and unrelated metadata.
        strings = ["manifest", "application", "meta-data", "name", "value",
                   "http://schemas.android.com/apk/res/android",
                   "unity.render-outside-safearea", "unrelated", "false"]
        offsets, blob = [], b""
        for s in strings:
            offsets.append(len(blob))
            blob += _encode_utf16_pool_string(s)
        start = 28 + 4 * len(strings)
        blob += b"\0" * (-(start + len(blob)) % 4)
        pool = struct.pack("<HHI5I", 1, 28, start + len(blob), len(strings), 0, 0, start, 0)
        pool += struct.pack(f"<{len(offsets)}I", *offsets) + blob
        resources = [0, 0, 0, 0x01010003, 0x01010024]
        resource_map = struct.pack("<HHI5I", 0x0180, 8, 28, *resources)

        def tag(name, attrs=()):
            return struct.pack("<HHI4I6H", 0x0102, 16, 36 + 20 * len(attrs),
                               0, 0xFFFFFFFF, 0xFFFFFFFF, strings.index(name), 20, 20, len(attrs), 0, 0, 0) + b"".join(attrs)

        def end(name):
            return struct.pack("<HHI4I", 0x0103, 16, 24, 0, 0xFFFFFFFF, 0xFFFFFFFF, strings.index(name))

        def attr(name, raw, kind, value):
            return struct.pack("<IIIHBBI", 5, strings.index(name), raw, 8, 0, kind, value)

        content = pool + resource_map + tag("manifest") + tag("application")
        for key, raw, kind, value in ((6, 8, 0x12, 0), (7, 0xFFFFFFFF, 0x10, 42)):
            content += tag("meta-data", [attr("name", key, 3, key), attr("value", raw, kind, value)]) + end("meta-data")
        content += end("application") + end("manifest")
        original = struct.pack("<HHI", 3, 8, 8 + len(content)) + content
        patched = patch_unity_window_metadata(original)
        self.assertEqual(patch_unity_window_metadata(patched), patched, "Duplicate metadata on repair")
        self.assertEqual(struct.unpack_from("<I", patched, 4)[0], len(patched))
        size = struct.unpack_from("<I", patched, 12)[0]
        parsed = _parse_string_pool(patched[8:8 + size])
        off, metadata, depth, app_depth = 8 + size, {}, 0, -1
        while off < len(patched):
            kind, header, size = struct.unpack_from("<HHI", patched, off)
            chunk = patched[off:off + size]
            if kind == 0x0102:
                element, attrs = _get_start_element_info(chunk, parsed)
                if element == "application":
                    app_depth = depth
                elif element == "meta-data":
                    self.assertEqual(depth, app_depth + 1, "Unity needs application, not activity metadata")
                    self.assertNotIn(attrs["name"], metadata)
                    metadata[attrs["name"]] = attrs["value"]
                    if attrs["name"] in UNITY_WINDOW_METADATA:
                        attr_start, width, count = struct.unpack_from("<HHH", chunk, 24)
                        values = [struct.unpack_from("<IIIHBBI", chunk, header + attr_start + i * width) for i in range(count)]
                        value = next(v for v in values if parsed[v[1]] == "value")
                        self.assertEqual((value[2], value[5], value[6]), (0xFFFFFFFF, 0x12, 0xFFFFFFFF))
                depth += 1
            elif kind == 0x0103:
                depth -= 1
            elif kind == 0x0180:
                ids = struct.unpack_from(f"<{(size - 8) // 4}I", chunk, 8)
                self.assertEqual(ids[parsed.index("name")], 0x01010003)
                self.assertEqual(ids[parsed.index("value")], 0x01010024)
            off += size
        self.assertEqual(metadata, {**dict.fromkeys(UNITY_WINDOW_METADATA, "0xffffffff"), "unrelated": "0x2a"})

    def test_production_window_lifecycle(self):
        java_home = Path(os.environ["JAVA_HOME"]) if os.environ.get("JAVA_HOME") else None
        javac = str(java_home / "bin" / "javac.exe") if java_home else shutil.which("javac")
        java = str(java_home / "bin" / "java.exe") if java_home else shutil.which("java")
        self.assertTrue(javac and java, "JDK required")
        with tempfile.TemporaryDirectory(prefix="bpp-window-tests-") as temporary:
            root = Path(temporary)
            sources = []
            for relative, code in STUBS.items():
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(code, encoding="utf-8")
                sources.append(str(target))
            production = Path(__file__).parent / "src/com/bepinex/android/bpp/BppGameWindow.java"
            subprocess.run([javac, "--release", "11", "-d", str(root), *sources, str(production)], check=True)
            subprocess.run([java, "-cp", str(root), "GameWindowTests"], check=True)


if __name__ == "__main__":
    unittest.main()
