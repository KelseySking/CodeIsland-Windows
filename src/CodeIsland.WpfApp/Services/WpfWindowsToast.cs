using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml.Linq;
using System.Windows;

namespace CodeIsland.WpfApp.Services;

/// <summary>
/// Action Center toast through combase WinRT. No NuGet. Failures are logged and swallowed.
/// </summary>
public static class WpfWindowsToast
{
    public const string SettingsKey = "windows_toast_enabled";
    public const string Aumid = "CodeIsland.Windows";

    private const int BodyMaxChars = 160;
    private static readonly Guid ToastFactoryIid = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid ManagerIid = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid XmlDocumentIoIid = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static int _registered;
    private static string? _iconPath;
    // ponytail: keep toast + handler alive so a later click still fires. Cap 32 pairs.
    private static readonly List<object> Live = [];

    public static void Show(string title, string body, Action? onActivate)
    {
        try
        {
            EnsureRegistered();
            _ = Task.Factory.StartNew(
                () => ShowOnMtaThread(Clamp(title), Clamp(string.IsNullOrWhiteSpace(body) ? " " : body), onActivate),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Log("queued");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
        }
    }

    private static void ShowOnMtaThread(string title, string body, Action? onActivate)
    {
        var hr = RoInitialize(RoInitMultithreaded);
        if (hr < 0)
        {
            Log($"RoInitialize hr=0x{hr:X8}");
            return;
        }

        try
        {
            var document = WinRt.Activate("Windows.Data.Xml.Dom.XmlDocument");
            var documentIo = document.QueryInterface(XmlDocumentIoIid);
            documentIo.CallHString(0, BuildXml(title, body));
            documentIo.Release();
            var toast = WinRt.Factory("Windows.UI.Notifications.ToastNotification", ToastFactoryIid)
                .CallReturning(0, document);
            if (onActivate != null)
            {
                var handler = new ActivatedHandler(onActivate);
                var punk = Marshal.GetComInterfaceForObject(handler, typeof(ITypedEventHandler));
                try
                {
                    // IToastNotification: slot 5 is Activated (slot 0 is Content).
                    toast.CallEvent(5, punk);
                }
                finally { Marshal.Release(punk); }
                lock (Live)
                {
                    if (Live.Count >= 64)
                        Live.RemoveRange(0, 2);
                    Live.Add(toast);
                    Live.Add(handler);
                }
            }

            var manager = WinRt.Factory("Windows.UI.Notifications.ToastNotificationManager", ManagerIid);
            // IToastNotificationManagerStatics: slot 0 is the no-argument
            // CreateToastNotifier; slot 1 is CreateToastNotifierWithId.
            var notifier = manager.CallReturningHString(1, Aumid);
            // IToastNotifier: slot 0 is Show(ToastNotification).
            notifier.CallVoid(0, toast.Ptr);
            notifier.Release();
            if (onActivate == null)
                toast.Release();
            document.Release();
            Log("shown");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
        }
        finally
        {
            RoUninitialize();
        }
    }

    public static void Trace(string line) => Log(line);

    private static void Log(string line)
    {
        var text = $"{DateTime.Now:O} {line}\n";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodeIsland");
            Directory.CreateDirectory(dir);
            using var stream = new FileStream(
                Path.Combine(dir, "toast.log"),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
        catch
        {
            try
            {
                var fallback = Path.Combine(Path.GetTempPath(), $"CodeIsland-toast-{Environment.ProcessId}.log");
                File.AppendAllText(fallback, text);
            }
            catch
            {
                // toast must never throw into the HUD
            }
        }
    }

    public static string Clamp(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= BodyMaxChars)
            return flat;
        return flat[..(BodyMaxChars - 1)] + "…";
    }

    internal static string BuildXml(string title, string body)
    {
        var visual = new XElement("visual",
            new XElement("binding",
                new XAttribute("template", "ToastGeneric"),
                new XElement("text", title),
                new XElement("text", body)));
        var icon = IconPath();
        if (icon != null)
        {
            visual.Element("binding")!.Add(new XElement("image",
                new XAttribute("placement", "appLogoOverride"),
                new XAttribute("src", icon)));
        }

        return new XElement("toast", visual).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>dotnet run -- --toast-self-check. Exit 0 and shows one toast.</summary>
    public static int SelfCheck()
    {
        Log("selfcheck-start");
        var xml = BuildXml("claude", "需要确认 Bash");
        if (!xml.Contains("ToastGeneric", StringComparison.Ordinal) || !xml.Contains("需要确认 Bash", StringComparison.Ordinal))
            return 2;
        if (Clamp(new string('a', 200)).Length != 160 || Clamp("a\nb").Contains('\n'))
            return 3;
        EnsureRegistered();
        Log("selfcheck-registered");
        var task = Task.Factory.StartNew(
            () => ShowOnMtaThread("CodeIsland", "通知自检", null),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        task.GetAwaiter().GetResult();
        Log("selfcheck-end");
        return 0;
    }

    private static void EnsureRegistered()
    {
        if (Volatile.Read(ref _registered) == 1)
            return;

        try
        {
            SetRegistryString($@"Software\Classes\AppUserModelId\{Aumid}", "DisplayName", "CodeIsland");
            var exe = ExecutablePath();
            var icon = IconPath();
            if (icon != null)
                SetRegistryString($@"Software\Classes\AppUserModelId\{Aumid}", "IconUri", icon);

            if (exe != null)
            {
                try { EnsureStartMenuShortcut(exe); }
                catch (Exception ex) { Log($"shortcut registration failed: {ex}"); }
                Log($"aumid hr=0x{SetCurrentProcessExplicitAppUserModelID(Aumid):X8}");
                var linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CodeIsland-Windows.lnk");
                Log($"shortcut {linkPath} exists={File.Exists(linkPath)}");
            }

            Volatile.Write(ref _registered, 1);
        }
        catch (Exception ex)
        {
            Log($"aumid registration failed: {ex}");
        }
    }

    private static void SetRegistryString(string subKey, string valueName, string value)
    {
        var data = Encoding.Unicode.GetBytes(value + "\0");
        var status = RegSetKeyValue(
            CurrentUser,
            subKey,
            valueName,
            RegSz,
            data,
            (uint)data.Length);
        if (status != 0)
            Marshal.ThrowExceptionForHR((int)(0x80070000u | status));
    }

    // Action Center only lists an unpackaged app that has a Start Menu .lnk carrying its AUMID.
    private static void EnsureStartMenuShortcut(string exe)
    {
        var linkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "CodeIsland-Windows.lnk");
        var shell = (IWshShell)Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(linkPath);
        if (!string.Equals(link.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
        {
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.Description = "CodeIsland";
            link.Save();
        }

        var store = (IPropertyStore)new ShellLink();
        var file = (IPersistFile)store;
        file.Load(linkPath, 2); // STGM_READWRITE
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        var value = PropVariant.FromString(Aumid);
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }

        file.Save(linkPath, true);
    }

    private static string? IconPath()
    {
        if (_iconPath != null && File.Exists(_iconPath))
            return _iconPath;

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeIsland");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "toast-icon.png");
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute));
                if (resource == null)
                    return null;
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                resource.Stream.CopyTo(output);
            }

            _iconPath = path;
            return path;
        }
        catch (Exception ex)
        {
            Log($"icon materialization failed: {ex}");
            return null;
        }
    }

    private static string? ExecutablePath()
    {
        var path = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static class WinRt
    {
        public static WinRtObject Activate(string className) => Unwrap(className, IInspectableIid, true);

        public static WinRtObject Factory(string className, Guid iid) => Unwrap(className, iid, false);

        private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");

        // WinRT objects answer to IInspectable, which the CLR marshaler does not speak.
        private static WinRtObject Unwrap(string className, Guid iid, bool instance)
        {
            var name = HString.Create(className);
            try
            {
                IntPtr raw;
                var hr = instance
                    ? RoActivateInstance(name, out raw)
                    : RoGetActivationFactory(name, ref iid, out raw);
                Check(hr);
                return new WinRtObject(raw);
            }
            finally
            {
                HString.Delete(name);
            }
        }

        private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    }

    /// <summary>
    /// One raw WinRT pointer. Slot 0 is the first method past the six IInspectable vtable
    /// slots, and every string travels as an HSTRING.
    /// </summary>
    private sealed class WinRtObject
    {
        private IntPtr _ptr;

        public WinRtObject(IntPtr ptr) => _ptr = ptr;

        public IntPtr Ptr => _ptr;

        public WinRtObject QueryInterface(Guid iid)
        {
            var self = Borrow();
            try
            {
                Check(QueryInterfaceCall(self, ref iid, out var result));
                return new WinRtObject(result);
            }
            finally { Marshal.Release(self); }
        }

        public void CallHString(int slot, string arg)
        {
            var value = HString.Create(arg);
            var self = Borrow();
            try { Check(SlotHString(self, slot)(self, value)); }
            finally
            {
                Marshal.Release(self);
                HString.Delete(value);
            }
        }

        public void CallVoid(int slot, IntPtr arg)
        {
            var self = Borrow();
            try { Check(SlotVoid(self, slot)(self, arg)); }
            finally { Marshal.Release(self); }
        }

        public void CallEvent(int slot, IntPtr handler)
        {
            var self = Borrow();
            try { Check(SlotEvent(self, slot)(self, handler, out long _)); }
            finally { Marshal.Release(self); }
        }

        public WinRtObject CallReturning(int slot, WinRtObject arg) => CallReturningPtr(slot, arg._ptr);

        private WinRtObject CallReturningPtr(int slot, IntPtr arg)
        {
            var self = Borrow();
            try
            {
                Check(Slot2(self, slot)(self, arg, out var created));
                return new WinRtObject(created);
            }
            finally { Marshal.Release(self); }
        }

        public WinRtObject CallReturning(int slot)
        {
            var self = Borrow();
            try
            {
                Check(Slot0(self, slot)(self, out var created));
                return new WinRtObject(created);
            }
            finally { Marshal.Release(self); }
        }

        public WinRtObject CallReturningHString(int slot, string arg)
        {
            var value = HString.Create(arg);
            var self = Borrow();
            try
            {
                Check(Slot2(self, slot)(self, value, out var created));
                return new WinRtObject(created);
            }
            finally
            {
                Marshal.Release(self);
                HString.Delete(value);
            }
        }

        public void Release()
        {
            var ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
                Marshal.Release(ptr);
        }

        private IntPtr Borrow()
        {
            var ptr = _ptr;
            if (ptr == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(WinRtObject));
            Marshal.AddRef(ptr);
            return ptr;
        }

        private static VtableCallHString SlotHString(IntPtr self, int slotPastInspectable) =>
            Marshal.GetDelegateForFunctionPointer<VtableCallHString>(Entry(self, slotPastInspectable));

        private static VtableCallVoid SlotVoid(IntPtr self, int slotPastInspectable) =>
            Marshal.GetDelegateForFunctionPointer<VtableCallVoid>(Entry(self, slotPastInspectable));

        private static VtableCallEvent SlotEvent(IntPtr self, int slotPastInspectable) =>
            Marshal.GetDelegateForFunctionPointer<VtableCallEvent>(Entry(self, slotPastInspectable));

        private static VtableCall0 Slot0(IntPtr self, int slotPastInspectable) =>
            Marshal.GetDelegateForFunctionPointer<VtableCall0>(Entry(self, slotPastInspectable));

        private static VtableCall2 Slot2(IntPtr self, int slotPastInspectable) =>
            Marshal.GetDelegateForFunctionPointer<VtableCall2>(Entry(self, slotPastInspectable));

        private static IntPtr Entry(IntPtr self, int slotPastInspectable) =>
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), (slotPastInspectable + 6) * IntPtr.Size);

        private static int QueryInterfaceCall(IntPtr self, ref Guid iid, out IntPtr result) =>
            Marshal.GetDelegateForFunctionPointer<VtableQueryInterface>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self)))
                (self, ref iid, out result);

        private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableCallHString(IntPtr self, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableCallVoid(IntPtr self, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableCallEvent(IntPtr self, IntPtr handler, out long token);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableCall0(IntPtr self, out IntPtr created);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableCall2(IntPtr self, IntPtr a, out IntPtr created);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VtableQueryInterface(IntPtr self, ref Guid iid, out IntPtr result);
    }

    private static class HString
    {
        public static IntPtr Create(string value)
        {
            var source = Marshal.StringToCoTaskMemUni(value);
            try
            {
                Marshal.ThrowExceptionForHR(WindowsCreateString(source, value.Length, out var handle));
                return handle;
            }
            finally { Marshal.FreeCoTaskMem(source); }
        }

        public static void Delete(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                WindowsDeleteString(handle);
        }

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            IntPtr sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;

        public static PropVariant FromString(string value)
        {
            var chars = (value.Length + 1) * 2;
            var buffer = Marshal.AllocCoTaskMem(chars);
            Marshal.Copy(Encoding.Unicode.GetBytes(value + "\0"), 0, buffer, chars);
            return new PropVariant { VariantType = 31, Pointer = buffer };
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static readonly IntPtr CurrentUser = new(unchecked((long)0x80000001u));
    private const uint RegSz = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegSetKeyValueW")]
    private static extern uint RegSetKeyValue(
        IntPtr hkey,
        string subKey,
        string valueName,
        uint type,
        byte[] data,
        uint dataSize);

    private const uint RoInitMultithreaded = 1;

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport]
    [Guid("F935DC21-1CF0-11D0-ADB9-00C04FD58A0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWshShell
    {
        [DispId(0x3ea)]
        IWshShortcut CreateShortcut(string pathLink);
    }

    [ComImport]
    [Guid("F935DC23-1CF0-11D0-ADB9-00C04FD58A0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWshShortcut
    {
        string TargetPath { get; set; }
        string Arguments { get; set; }
        string Description { get; set; }
        string WorkingDirectory { get; set; }
        void Save();
    }

    /// <summary>WinRT TypedEventHandler&lt;ToastNotification, object&gt; Invoke.</summary>
    [ComImport]
    [Guid("AB54DE2D-97D9-5528-B6AD-105AFE156530")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITypedEventHandler
    {
        void Invoke(IntPtr sender, IntPtr args);
    }

    [ComVisible(true)]
    private sealed class ActivatedHandler : ITypedEventHandler
    {
        private readonly Action _callback;
        public ActivatedHandler(Action callback) => _callback = callback;
        public void Invoke(IntPtr sender, IntPtr args) => _callback();
    }
}
