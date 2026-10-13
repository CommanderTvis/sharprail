using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpRail.Host.Core;

[SupportedOSPlatform("windows")]
internal static class WindowsTrash
{
    internal static void Move(string path)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Recycle(path); completed.SetResult(); }
            catch (Exception error) { completed.SetException(new IOException("The file could not be moved to the Recycle Bin.", error)); }
        })
        { IsBackground = true, Name = "SharpRail Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Task.GetAwaiter().GetResult();
    }

    private static void Recycle(string path)
    {
        var classId = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        var operationId = typeof(IFileOperation).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref operationId, out var operation));
        nint item = 0;
        try
        {
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref itemId, out item));
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x00000400 | 0x00000010 | 0x00000004);
            operation.DeleteItem(item, 0);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || File.Exists(path) || Directory.Exists(path)) throw new IOException("Windows did not recycle the item.");
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IFileOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid interfaceId, out nint item);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(nint item, nint destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(nint item, nint sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}