package com.bepinex.android.bpp;

import java.util.Locale;

/** Platform-specific launch policy that remains independent of Android APIs for host testing. */
public final class BppPlatformCompatibility {

    private BppPlatformCompatibility() {}

    public static boolean isHarmonyAnco(
        String arkAncoVersion,
        String ancoApiVersion,
        String ancoEnabled,
        String manufacturer,
        int sdkInt,
        String display
    ) {
        if (hasEnabledValue(arkAncoVersion) || hasEnabledValue(ancoApiVersion)) return true;
        if (isTrue(ancoEnabled)) return true;

        // HarmonyOS NEXT exposes the properties above, but retain a narrow fallback for devices
        // where the compatibility layer blocks getprop. This combination is present in the
        // captured ANCO environment and does not match existing Huawei Android releases.
        return "huawei".equals(normalize(manufacturer))
            && sdkInt >= 36
            && normalize(display).startsWith("system ");
    }

    private static boolean hasEnabledValue(String value) {
        String normalized = normalize(value);
        return !normalized.isEmpty()
            && !"0".equals(normalized)
            && !"false".equals(normalized)
            && !"unknown".equals(normalized);
    }

    private static boolean isTrue(String value) {
        String normalized = normalize(value);
        return "1".equals(normalized) || "true".equals(normalized) || "yes".equals(normalized);
    }

    private static String normalize(String value) {
        return value == null ? "" : value.trim().toLowerCase(Locale.ROOT);
    }
}
