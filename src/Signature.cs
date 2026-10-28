using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Lighter
{
    enum SigState { NoFile, Unsigned, Invalid, Signed }

    class SigInfo
    {
        public SigState State;
        /// <summary>Who signed it (certificate name), when the signature checks out.</summary>
        public string Signer = "";

        public bool IsMicrosoft
        {
            get
            {
                return State == SigState.Signed && (Signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
                    || Signer.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase));
            }
        }

        public static readonly SigInfo NoFile = new SigInfo { State = SigState.NoFile };
    }

    /// <summary>
    /// Authenticode check like Sysinternals Autoruns: the file's own signature, or — for Windows files that
    /// are signed through a catalog — the catalog that lists the file's hash. Offline: no revocation checks,
    /// no downloads. Results are cached per file (and per catalog, which are large and shared by many files).
    /// </summary>
    static class Signature
    {
        static readonly Dictionary<string, SigInfo> files = new Dictionary<string, SigInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, SigInfo> catalogs = new Dictionary<string, SigInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Checks every item's file (in parallel, each file once) and sets Sig and the derived flags.</summary>
        public static void Apply(IEnumerable<AutorunItem> items)
        {
            var list = items.ToList();
            foreach (var it in list)
            {
                // rundll32 x.dll, powershell script.ps1 ...: the file it runs is what counts, not the Microsoft-signed
                // launcher. A launcher with nothing to check (powershell -enc ...) is never "system".
                if (it.Kind == SourceKind.Service || !PathUtil.IsLauncher(it.SignPath)) continue;
                string target = PathUtil.FileInArgs(it.Command);
                if (target != null)
                {
                    it.SignPath = target;
                    it.Publisher = PathUtil.GetPublisher(target);
                }
                else it.AlwaysShow = true;
            }
            var paths = list.Select(i => i.SignPath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, p => Check(p));
            foreach (var it in list)
            {
                it.Sig = string.IsNullOrEmpty(it.SignPath) ? SigInfo.NoFile : Check(it.SignPath);
                // Plain Run keys and Startup folders stay visible as in Task Manager; elsewhere (services, tasks,
                // Winlogon, Active Setup...) what Microsoft signed is part of Windows.
                bool plain = (it.Kind == SourceKind.Registry && it.Special == Special.None) || it.Kind == SourceKind.StartupFolder;
                if (it.Sig.IsMicrosoft && !it.AlwaysShow && !plain) it.IsSystem = true;
                if (it.Sig.State == SigState.Signed) it.Publisher = it.Sig.Signer;
            }
        }

        public static SigInfo Check(string path)
        {
            if (!PathUtil.SafeExists(path)) return SigInfo.NoFile;
            lock (files)
            {
                SigInfo cached;
                if (files.TryGetValue(path, out cached)) return cached;
            }
            SigInfo result;
            try { result = Verify(path); }
            catch { result = new SigInfo { State = SigState.Unsigned }; }
            lock (files) files[path] = result;
            return result;
        }

        static SigInfo Verify(string path)
        {
            string signer;
            uint rc = VerifyFile(path, out signer);
            if (rc == 0) return new SigInfo { State = SigState.Signed, Signer = signer };
            if (rc != TRUST_E_NOSIGNATURE && rc != TRUST_E_SUBJECT_FORM_UNKNOWN && rc != TRUST_E_PROVIDER_UNKNOWN)
                return new SigInfo { State = SigState.Invalid };

            string cat = FindCatalog(path);
            if (cat == null) return new SigInfo { State = SigState.Unsigned };
            lock (catalogs)
            {
                SigInfo c;
                if (catalogs.TryGetValue(cat, out c)) return c;
            }
            rc = VerifyFile(cat, out signer);
            var info = rc == 0 ? new SigInfo { State = SigState.Signed, Signer = signer } : new SigInfo { State = SigState.Invalid };
            lock (catalogs) catalogs[cat] = info;
            return info;
        }

        // ----- WinVerifyTrust -----

        const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
        const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public int dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, int signerIdx, bool counterSigner, int counterSignerIdx);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
        static extern int CertGetNameString(IntPtr cert, int type, int flags, IntPtr typePara, StringBuilder name, int size);

        /// <summary>WinVerifyTrust on one file (embedded signature or a .cat); signer = certificate name.</summary>
        static uint VerifyFile(string path, out string signer)
        {
            signer = "";
            var fi = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                    // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL: never goes online
                    dwProvFlags = 0x10 | 0x1000,
                };
                uint rc = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                try
                {
                    if (rc == 0) signer = SignerName(data.hWVTStateData);
                }
                finally
                {
                    data.dwStateAction = 2;    // WTD_STATEACTION_CLOSE
                    WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                }
                return rc;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }

        static string SignerName(IntPtr state)
        {
            IntPtr prov = WTHelperProvDataFromStateData(state);
            if (prov == IntPtr.Zero) return "";
            IntPtr sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (sgnr == IntPtr.Zero) return "";
            // CRYPT_PROVIDER_SGNR: cbStruct, FILETIME sftVerifyAsOf, csCertChain, pasCertChain (offset 16).
            IntPtr chain = Marshal.ReadIntPtr(sgnr, 16);
            if (chain == IntPtr.Zero || Marshal.ReadInt32(sgnr, 12) == 0) return "";
            // CRYPT_PROVIDER_CERT: cbStruct, then pCert (aligned to a pointer).
            IntPtr cert = Marshal.ReadIntPtr(chain, IntPtr.Size);
            if (cert == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            CertGetNameString(cert, 4 /* CERT_NAME_SIMPLE_DISPLAY_TYPE */, 0, IntPtr.Zero, sb, sb.Capacity);
            return sb.ToString();
        }

        // ----- catalogs -----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CATALOG_INFO
        {
            public int cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, int flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminAcquireContext(out IntPtr catAdmin, IntPtr subsystem, int flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, int flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, IntPtr file, ref int hashSize, byte[] hash, int flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminCalcHashFromFileHandle(IntPtr file, ref int hashSize, byte[] hash, int flags);

        [DllImport("wintrust.dll")]
        static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, int hashSize, int flags, IntPtr prevCatInfo);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, int flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, int flags);

        /// <summary>The system catalog (.cat) that lists the file's hash: SHA-256 catalogs first, then the older SHA-1 ones.</summary>
        static string FindCatalog(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                IntPtr h = fs.SafeFileHandle.DangerousGetHandle();
                foreach (string alg in new[] { "SHA256", null })
                {
                    IntPtr admin;
                    bool ok;
                    try { ok = alg != null ? CryptCATAdminAcquireContext2(out admin, IntPtr.Zero, alg, IntPtr.Zero, 0) : CryptCATAdminAcquireContext(out admin, IntPtr.Zero, 0); }
                    catch (EntryPointNotFoundException) { continue; }
                    if (!ok) continue;
                    try
                    {
                        var hash = new byte[64];
                        int size = hash.Length;
                        fs.Position = 0;
                        bool hashed;
                        try { hashed = alg != null ? CryptCATAdminCalcHashFromFileHandle2(admin, h, ref size, hash, 0) : CryptCATAdminCalcHashFromFileHandle(h, ref size, hash, 0); }
                        catch (EntryPointNotFoundException) { continue; }
                        if (!hashed) continue;
                        IntPtr ci = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                        if (ci == IntPtr.Zero) continue;
                        try
                        {
                            var info = new CATALOG_INFO { cbStruct = Marshal.SizeOf(typeof(CATALOG_INFO)) };
                            if (CryptCATCatalogInfoFromContext(ci, ref info, 0) && !string.IsNullOrEmpty(info.wszCatalogFile))
                                return info.wszCatalogFile;
                        }
                        finally { CryptCATAdminReleaseCatalogContext(admin, ci, 0); }
                    }
                    finally { CryptCATAdminReleaseContext(admin, 0); }
                }
            }
            return null;
        }
    }
}
