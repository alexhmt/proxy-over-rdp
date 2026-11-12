// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RdpProxy.Client;

/// <summary>
/// IWTSPlugin interface for RDP DVC plugins.
/// </summary>
[ComImport]
[Guid("A1230201-1439-4E62-A414-190D0AC3D40E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSPlugin
{
    [PreserveSig]
    int Initialize([In, MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannelManager pChannelMgr);
    
    [PreserveSig]
    int Connected();
    
    [PreserveSig]
    int Disconnected([In] uint dwDisconnectCode);
    
    [PreserveSig]
    int Terminated();
}

/// <summary>
/// IWTSVirtualChannelManager interface.
/// </summary>
[ComImport]
[Guid("A1230205-D6A7-11D8-B9FD-000BDBD1F198")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelManager
{
    [PreserveSig]
    int CreateListener(
        [In, MarshalAs(UnmanagedType.LPStr)] string pszChannelName,
        [In] uint uFlags,
        [In, MarshalAs(UnmanagedType.Interface)] IWTSListenerCallback pListenerCallback,
        [Out, MarshalAs(UnmanagedType.Interface)] out IWTSListener ppListener);
}

/// <summary>
/// IWTSListener interface.
/// </summary>
[ComImport]
[Guid("A1230206-9A39-4D58-8674-CDB4DFF4E73B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListener
{
    [PreserveSig]
    int GetConfiguration([Out, MarshalAs(UnmanagedType.Interface)] out object ppPropertyBag);
}

/// <summary>
/// IWTSListenerCallback interface.
/// </summary>
[ComImport]
[Guid("A1230203-D6A7-11D8-B9FD-000BDBD1F198")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListenerCallback
{
    [PreserveSig]
    int OnNewChannelConnection(
        [In, MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannel pChannel,
        [In, MarshalAs(UnmanagedType.BStr)] string data,
        [Out, MarshalAs(UnmanagedType.Bool)] out bool pbAccept,
        [Out, MarshalAs(UnmanagedType.Interface)] out IWTSVirtualChannelCallback ppCallback);
}

/// <summary>
/// IWTSVirtualChannel interface.
/// </summary>
[ComImport]
[Guid("A1230207-D6A7-11D8-B9FD-000BDBD1F198")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannel
{
    [PreserveSig]
    int Write(
        [In] uint cbSize,
        [In] IntPtr pBuffer,
        [In] IntPtr pReserved);

    [PreserveSig]
    int Close();
}

/// <summary>
/// IWTSVirtualChannelCallback interface.
/// </summary>
[ComImport]
[Guid("A1230204-D6A7-11D8-B9FD-000BDBD1F198")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelCallback
{
    [PreserveSig]
    int OnDataReceived(
        [In] uint cbSize,
        [In] IntPtr pBuffer);

    [PreserveSig]
    int OnClose();
}
