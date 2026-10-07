using System.Reflection;

[assembly: AssemblyTitle("Claude Code Traffic Light")]
[assembly: AssemblyProduct("Claude Code Traffic Light")]
[assembly: AssemblyDescription("Tray traffic light for Claude Code: status, notifications and answers from the phone")]
[assembly: AssemblyCompany("BelalovBM")]
[assembly: AssemblyCopyright("Copyright © 2026 BelalovBM")]
[assembly: AssemblyVersion("0.9.1.0")]
[assembly: AssemblyFileVersion("0.9.1.0")]
[assembly: AssemblyInformationalVersion("0.9.1-beta")]
// The unit tests (tests\, run by tools\run-tests.ps1 and on every GitHub build) check the logic of internal classes.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ClaudeCodeTrafficLight.Tests")]

namespace Semaphore
{
    static class AppInfo
    {
        public const string Author = "BelalovBM";
        public const string Year = "2026";
        public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";
        // Source code, new versions and problem reports.
        public const string RepoUrl = "https://github.com/BelalovBM/claude-code-traffic-light";
        // TRON network address for donations.
        public const string Wallet = "TUKeo9zL3YwwmBdh2pr2HfetANm32UtD4x";

        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); }
        }
    }
}
