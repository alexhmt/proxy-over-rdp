// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace RdpProxy.Client;

/// <summary>
/// Native method declarations for COM and Win32 APIs.
/// Simplified version for Simple plugin (no render hints).
/// </summary>
internal static class NativeMethods
{
    #region COM Registration

    public const int COINIT_MULTITHREADED = 0x0;
    public const int CLSCTX_LOCAL_SERVER = 0x4;
    public const int REGCLS_MULTIPLEUSE = 1;
    public const int REGCLS_SUSPENDED = 4;

    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);

    [DllImport("Ole32")]
    public static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("Ole32")]
    public static extern void CoUninitialize();

    [DllImport("Ole32")]
    public static extern int CoRegisterClassObject(
        ref Guid guid,
        [MarshalAs(UnmanagedType.IUnknown)] object obj,
        int context,
        int flags,
        out int register);

    [DllImport("Ole32")]
    public static extern int CoResumeClassObjects();

    [DllImport("Ole32")]
    public static extern int CoRevokeClassObject(int register);

    #endregion

    #region IUnknown / IClassFactory GUIDs

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");

    #endregion

    #region DVC Channel Flags

    public const uint CHANNEL_FLAG_FIRST = 0x01;
    public const uint CHANNEL_FLAG_LAST = 0x02;

    #endregion
}
