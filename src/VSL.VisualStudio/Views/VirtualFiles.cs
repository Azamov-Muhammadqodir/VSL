using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace VSL.VisualStudio.Views;

/// <summary>
/// Files on the clipboard that have no path of their own (FileGroupDescriptorW + FileContents): an attachment copied
/// from Outlook, a file from a browser download list or from inside a zip. They are saved to a temp folder so they can
/// be attached like any other file.
/// </summary>
static class VirtualFiles
{
    /// <summary>Larger files are left out: Claude reads them from the solution better than from a copy.</summary>
    const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>FILEDESCRIPTORW: 72 bytes of flags, sizes and times, then a 260-character name.</summary>
    const int DescriptorSize = 592;
    const int NameOffset = 72;

    public static List<string> Save(IDataObject data)
    {
        var saved = new List<string>();
        if (data.GetData("FileGroupDescriptorW") is not MemoryStream descriptorStream || data is not ComDataObject com)
            return saved;
        var descriptor = descriptorStream.ToArray();
        if (descriptor.Length < 4)
            return saved;
        var count = BitConverter.ToInt32(descriptor, 0);
        var folder = Path.Combine(Path.GetTempPath(), "VSL Claude", "pasted", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var contents = (short)DataFormats.GetDataFormat("FileContents").Id;
        for (var i = 0; i < count && 4 + (i + 1) * DescriptorSize <= descriptor.Length; i++)
        {
            var raw = Encoding.Unicode.GetString(descriptor, 4 + i * DescriptorSize + NameOffset, 520);
            var name = Path.GetFileName(raw.Split('\0')[0]);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;
            if (Read(com, contents, i) is not { } bytes)
                continue;
            // A global memory block can be larger than the file: the descriptor has the real size (FD_FILESIZE).
            var at = 4 + i * DescriptorSize;
            if ((BitConverter.ToUInt32(descriptor, at) & 0x40) != 0)
            {
                var size = ((long)BitConverter.ToUInt32(descriptor, at + 64) << 32) | BitConverter.ToUInt32(descriptor, at + 68);
                if (size < bytes.Length)
                    Array.Resize(ref bytes, (int)size);
            }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            saved.Add(path);
        }
        return saved;
    }

    static byte[]? Read(ComDataObject data, short format, int index)
    {
        var request = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM,
        };
        STGMEDIUM medium;
        try
        {
            data.GetData(ref request, out medium);
        }
        catch (COMException)
        {
            return null;
        }
        try
        {
            return medium.tymed switch
            {
                TYMED.TYMED_HGLOBAL => FromGlobal(medium.unionmember),
                TYMED.TYMED_ISTREAM => FromStream((IStream)Marshal.GetObjectForIUnknown(medium.unionmember)),
                _ => null,
            };
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static byte[]? FromGlobal(IntPtr handle)
    {
        var size = (long)GlobalSize(handle);
        if (size <= 0 || size > MaxBytes)
            return null;
        var pointer = GlobalLock(handle);
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    static byte[]? FromStream(IStream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, readPointer);
                var read = Marshal.ReadInt32(readPointer);
                if (read <= 0)
                    break;
                output.Write(buffer, 0, read);
                if (output.Length > MaxBytes)
                    return null;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(readPointer);
        }
        return output.ToArray();
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
