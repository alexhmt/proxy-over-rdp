// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RdpProxy.Client;

/// <summary>
/// COM LocalServer helper for Simple plugin.
/// </summary>
internal sealed class LocalServer : IDisposable
{
    private readonly List<int> _registrationCookies = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _comInitialized;

    public LocalServer()
    {
        // Initialize COM in MTA mode (required for DVC plugins)
        int hr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        _comInitialized = true;
        Log("COM initialized (MTA)");
    }

    /// <summary>
    /// Registers a CLSID -> IClassFactory mapping with the COM runtime.
    /// </summary>
    public void RegisterClass<T>(Guid clsid) where T : class, new()
    {
        RequireOwnerThread();
        Log($"Registering class object:");
        Log($"  CLSID: {clsid:B}");
        Log($"  Type : {typeof(T)}");

        int hr = NativeMethods.CoRegisterClassObject(
            ref clsid,
            new SimpleClassFactory<T>(),
            NativeMethods.CLSCTX_LOCAL_SERVER,
            NativeMethods.REGCLS_MULTIPLEUSE | NativeMethods.REGCLS_SUSPENDED,
            out int cookie);

        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        _registrationCookies.Add(cookie);
        Log($"  Cookie: {cookie}");

        // Resume class objects to allow activation
        hr = NativeMethods.CoResumeClassObjects();
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        Log("Class object registered and resumed");
    }

    /// <summary>
    /// Keeps the owning MTA thread alive until the tray host requests shutdown.
    /// </summary>
    public void Run(CancellationToken ct = default)
    {
        RequireOwnerThread();
        Log("LocalServer running");
        ct.WaitHandle.WaitOne();
        Log("Shutting down");
    }

    public void StopActivations()
    {
        RequireOwnerThread();
        Log("Revoking class object registrations:");
        Exception? failure = null;
        foreach (int cookie in _registrationCookies.ToArray())
        {
            Log($"  Cookie: {cookie}");
            int hr = NativeMethods.CoRevokeClassObject(cookie);
            if (hr < 0) failure ??= Marshal.GetExceptionForHR(hr);
            else _registrationCookies.Remove(cookie);
        }
        if (failure != null) throw failure;
    }

    public void Dispose()
    {
        RequireOwnerThread();
        try { StopActivations(); }
        finally
        {
            if (_comInitialized)
            {
                NativeMethods.CoUninitialize();
                _comInitialized = false;
                Log("COM uninitialized");
            }
        }
    }

    private void RequireOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("COM LocalServer must stay on its owning MTA thread.");
    }

    private static void Log(string message)
    {
        Diagnostics.Write("local-server " + message);
        Console.WriteLine($"[LocalServer] {message}");
    }
}
