package com.bazzarhelper.android.runtime;

import android.os.*;
import java.util.*;

/** Public fields are the pinned native bootstrap ABI. */
public final class RuntimeConfig implements Parcelable {
  public static final String EXTRA = "bazaarhelper.runtime.config";

  public String gameLibraryDirectory,
      appLibraryDirectory,
      appDataDirectory,
      codeCacheDirectory,
      bepInExDirectory,
      dotnetDirectory,
      unityDataDirectory,
      unityVersion;
  public boolean isIl2Cpp2Mono = false, useOriginalLibUnity = true;
  public String[] fusionVariables = new String[0], auxiliaryPluginFolders = new String[0];
  public Map<String, String> il2cppApiMap = new HashMap<>();

  public RuntimeConfig() {}

  private RuntimeConfig(Parcel p) {
    gameLibraryDirectory = p.readString();
    appLibraryDirectory = p.readString();
    appDataDirectory = p.readString();
    codeCacheDirectory = p.readString();
    bepInExDirectory = p.readString();
    dotnetDirectory = p.readString();
    unityDataDirectory = p.readString();
    unityVersion = p.readString();
  }

  public void writeToParcel(Parcel p, int flags) {
    for (String s :
        new String[] {
          gameLibraryDirectory,
          appLibraryDirectory,
          appDataDirectory,
          codeCacheDirectory,
          bepInExDirectory,
          dotnetDirectory,
          unityDataDirectory,
          unityVersion
        }) p.writeString(s);
  }

  public int describeContents() {
    return 0;
  }

  public static final Creator<RuntimeConfig> CREATOR =
      new Creator<RuntimeConfig>() {
        public RuntimeConfig createFromParcel(Parcel p) {
          return new RuntimeConfig(p);
        }

        public RuntimeConfig[] newArray(int n) {
          return new RuntimeConfig[n];
        }
      };
}
