using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace SharpRail.Host.Core;

[SupportedOSPlatform("windows")]
internal static partial class WindowsTrash
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
        var operationId = new Guid(FileOperationId);
        Marshal.ThrowExceptionForHR(CoCreateInstance(classId, 0, 1, operationId, out var instance));
        IFileOperation operation;
        try { operation = (IFileOperation)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(instance); }
        nint item = 0;
        try
        {
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, itemId, out item));
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x00000400 | 0x00000010 | 0x00000004);
            operation.DeleteItem(item, 0);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || File.Exists(path) || Directory.Exists(path)) throw new IOException("Windows did not recycle the item.");
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            ((ComObject)(object)operation).FinalRelease();
        }
    }

    private const string FileOperationId = "947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8";

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint context, in Guid interfaceId, out nint item);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid(FileOperationId)]
    internal partial interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, string name, nint sink);
        void RenameItems(nint items, string name);
        void MoveItem(nint item, nint destination, string name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, string name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(nint item, nint sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, string name, string template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}