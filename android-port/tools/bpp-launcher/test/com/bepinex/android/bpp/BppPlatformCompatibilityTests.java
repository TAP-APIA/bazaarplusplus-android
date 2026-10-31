package com.bepinex.android.bpp;

public final class BppPlatformCompatibilityTests {

    public static void main(String[] args) {
        check(isAnco("24.0.0.0", "", "", "HUAWEI", 36, "System 104"), "ark property");
        check(isAnco("", "260000", "", "HUAWEI", 36, "System 104"), "api property");
        check(isAnco("", "", "true", "HUAWEI", 36, "System 104"), "enable property");
        check(isAnco("", "", "", "HUAWEI", 36, "System 104.3.5"), "ANCO build fallback");
        check(!isAnco("", "", "", "HUAWEI", 35, "System 104"), "old Huawei Android");
        check(!isAnco("0", "0", "false", "Google", 36, "BP2A"), "ordinary Android");
        check(!isAnco(null, null, null, null, 0, null), "missing values");
        System.out.println("Harmony ANCO compatibility tests passed");
    }

    private static boolean isAnco(
        String ark,
        String api,
        String enabled,
        String manufacturer,
        int sdk,
        String display
    ) {
        return BppPlatformCompatibility.isHarmonyAnco(
            ark,
            api,
            enabled,
            manufacturer,
            sdk,
            display
        );
    }

    private static void check(boolean value, String message) {
        if (!value) throw new AssertionError(message);
    }
}
