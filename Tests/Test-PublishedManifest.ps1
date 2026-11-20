param(
    [Parameter(Mandatory)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
if (-not ('ReadingTimeManagerChecks.Manifest' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ReadingTimeManagerChecks
{
    public static class Manifest
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ActivationContext
        {
            public uint Size;
            public uint Flags;
            public string Source;
            public ushort ProcessorArchitecture;
            public ushort LanguageId;
            public string AssemblyDirectory;
            public IntPtr ResourceName;
            public string ApplicationName;
            public IntPtr Module;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr CreateActCtxW(ref ActivationContext context);

        [DllImport("kernel32.dll")]
        private static extern void ReleaseActCtx(IntPtr handle);

        public static void Validate(string executable)
        {
            const uint ResourceNameValid = 0x008;
            var context = new ActivationContext
            {
                Size = (uint)Marshal.SizeOf(typeof(ActivationContext)),
                Flags = ResourceNameValid,
                Source = executable,
                ResourceName = new IntPtr(1)
            };
            var handle = CreateActCtxW(ref context);
            if (handle == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            ReleaseActCtx(handle);
        }
    }
}
'@
}

[ReadingTimeManagerChecks.Manifest]::Validate($resolvedExe)
Write-Output "PASS: Windows accepted the embedded manifest in $resolvedExe"
