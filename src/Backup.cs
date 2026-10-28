using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>Copies of everything Lighter deletes, in %LOCALAPPDATA%\Lighter\Backup.</summary>
    static class Backup
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lighter", "Backup");

        static string NewPath(string name, string ext)
        {
            Directory.CreateDirectory(Dir);
            string safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + ext);
        }

        public static string SaveText(string name, string ext, string text)
        {
            string path = NewPath(name, ext);
            File.WriteAllText(path, text, Encoding.Unicode);
            return path;
        }

        public static string MoveFile(string src)
        {
            string dest = NewPath(Path.GetFileNameWithoutExtension(src), Path.GetExtension(src));
            File.Move(src, dest);
            return dest;
        }

        /// <summary>Writes one registry value as a .reg file that can be double-clicked to restore it.</summary>
        public static string ExportRegValue(string keyFullName, string valueName, object value, RegistryValueKind kind, string fileName = null)
        {
            var sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            sb.Append("[").Append(keyFullName).Append("]\r\n");
            sb.Append(RegQuote(valueName)).Append('=');
            switch (kind)
            {
                case RegistryValueKind.String:
                    sb.Append(RegQuote((string)value));
                    break;
                case RegistryValueKind.DWord:
                    sb.Append("dword:").Append(((int)value).ToString("x8"));
                    break;
                case RegistryValueKind.ExpandString:
                    sb.Append("hex(2):").Append(Hex(Encoding.Unicode.GetBytes((string)value + "\0")));
                    break;
                case RegistryValueKind.MultiString:
                    sb.Append("hex(7):").Append(Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])value) + "\0\0")));
                    break;
                case RegistryValueKind.QWord:
                    sb.Append("hex(b):").Append(Hex(BitConverter.GetBytes((long)value)));
                    break;
                default:
                    sb.Append("hex:").Append(Hex(value as byte[] ?? new byte[0]));
                    break;
            }
            sb.Append("\r\n");
            return SaveText(fileName ?? valueName, ".reg", sb.ToString());
        }

        static string RegQuote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static string Hex(byte[] bytes)
        {
            return string.Join(",", bytes.Select(b => b.ToString("x2")));
        }
    }
}
