// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace RdpProxy.Client;

/// <summary>
/// Simple IClassFactory implementation for COM LocalServer.
/// Creates instances of the specified type T.
/// </summary>
[ComVisible(true)]
[Guid("00000001-0000-0000-C000-000000000046")]
public sealed class SimpleClassFactory<T> : IClassFactory where T : class, new()
{
    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        // Aggregation not supported
        if (pUnkOuter != IntPtr.Zero)
            return NativeMethods.CLASS_E_NOAGGREGATION;

        // Create instance
        var instance = new T();

        // Get IUnknown for the instance
        IntPtr punk = Marshal.GetIUnknownForObject(instance);
        try
        {
            // Query for requested interface
            int hr = Marshal.QueryInterface(punk, ref riid, out ppvObject);
            return hr;
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    public int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock)
    {
        // Not implemented - LocalServer lifetime managed by COM runtime
        return 0; // S_OK
    }
}

/// <summary>
/// IClassFactory COM interface.
/// </summary>
[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(
        IntPtr pUnkOuter,
        ref Guid riid,
        out IntPtr ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
