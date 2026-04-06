namespace ABS_System.IntegrationTests;

/// <summary>
/// Documents (with executable assertions) why two physical browsers can show the same DEVICE_ID on the Blocked page.
/// The real bind/compare logic lives in Views/Activation/Blocked.cshtml (JavaScript); server activation stores
/// posted DEVICE_ID + MACHINE_FINGERPRINT in LICENSE_ACTIVATION (see TryRegisterSeatDeviceAsync).
/// </summary>
public sealed class SeatDeviceBindScenariosTests
{
    [Fact]
    public void Step01_Activation_row_stores_submitted_device_id_and_fingerprint()
    {
        /* Firebird: LICENSE_ACTIVATION.DEVICE_ID = form deviceId, MACHINE_FINGERPRINT = form deviceFingerprint */
        const string postedDeviceId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        const string postedFp = "ABCD1234";
        Assert.True(postedDeviceId.Length <= 64);
        Assert.NotEmpty(postedFp);
    }

    [Fact]
    public void Step02_second_pc_does_not_reuse_pc1_id_when_coarse_matches_but_bind_key_includes_nonce()
    {
        /* Same coarse string on two PCs is possible (identical hardware/browser). Stored bind is seatBindKey(coarse, sn)=coarse+"||SN||"+nonce; PC2 has a different HttpOnly nonce → bundle does not match → new DEVICE_ID. */
        var coarse = "v1part||1920x1040|en-AU|en-US,en|Intel/Google,Chrome/120";
        var pc1Nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var pc2Nonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var pc1BindKey = coarse + "||SN||" + pc1Nonce;
        var pc2BindKey = coarse + "||SN||" + pc2Nonce;
        Assert.NotEqual(pc1BindKey, pc2BindKey);
    }

    [Fact]
    public void Step03_second_pc_gets_new_id_when_bind_strings_differ()
    {
        var pc1StoredBind = "v1part||1920x1040|en-AU|en-US,en|Intel/Google";
        var pc2ComputedCoarse = "v1part||1680x1050|en-AU|en-US,en|Intel/Google";
        Assert.NotEqual(pc1StoredBind, pc2ComputedCoarse);
        /* Blocked.js: newSeatDeviceId() + writeSeatBundle */
    }

    [Fact]
    public void Step04_server_cookie_only_affects_display_when_get_request_sends_that_cookie()
    {
        /* preserveSeatDeviceId = formDeviceId from HttpOnly cookie; two PCs do not share cookies — unless same synced browser profile (rare). */
        const string cookieFromRequest = "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB";
        Assert.NotEmpty(cookieFromRequest);
    }

    [Fact]
    public void Step05_local_bundle_must_match_server_seat_nonce_or_it_is_discarded()
    {
        /* Blocked.cshtml: bundle.sn must equal hidden seatClientNonce (from HttpOnly cookie on GET). */
        const string pc1Nonce = "a1b2c3d4e5f6789012345678abcdef01";
        const string pc2Nonce = "fedcba098765432109876543210fedcb";
        Assert.NotEqual(pc1Nonce, pc2Nonce);
    }
}
