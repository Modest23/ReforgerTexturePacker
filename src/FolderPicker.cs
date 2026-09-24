using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // Folder picker using the modern Explorer-style dialog (IFileOpenDialog + FOS_PICKFOLDERS) -
    // the same window as OpenFileDialog, with the address bar, quick access and paste-a-path.
    // .NET Framework's FolderBrowserDialog only offers the old tree view.
    public static class FolderPicker
    {
        // Returns the chosen folder, or null when cancelled.
        public static string Pick(IWin32Window owner, string title, string initialDir)
        {
            IFileDialog dlg = null;
            try
            {
                dlg = (IFileDialog)new FileOpenDialogRCW();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                if (!string.IsNullOrEmpty(title))
                    dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                {
                    IShellItem folder;
                    Guid iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iid, out folder) == 0)
                        dlg.SetFolder(folder);
                }
                int hr = dlg.Show(owner != null ? owner.Handle : IntPtr.Zero);
                if (hr != 0)
                    return null; // cancelled (0x800704C7) or failed
                IShellItem result;
                dlg.GetResult(out result);
                IntPtr psz;
                result.GetDisplayName(SIGDN_FILESYSPATH, out psz);
                try { return Marshal.PtrToStringUni(psz); }
                finally { Marshal.FreeCoTaskMem(psz); }
            }
            catch (Exception)
            {
                // COM unavailable for some reason - fall back to the classic dialog rather than failing.
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.Description = title;
                    if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                        fb.SelectedPath = initialDir;
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
                }
            }
            finally
            {
                if (dlg != null)
                    Marshal.ReleaseComObject(dlg);
            }
        }

        private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        // Vtable order matters: IModalWindow.Show first, then IFileDialog's methods as declared in shobjidl.h.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
