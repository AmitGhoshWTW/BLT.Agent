using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace BLT.Agent.Services
{
    public class SessionHelper
    {
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        uint wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

        // ── Constants ─────────────────────────────────────────────────────────────────
        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const uint LOGON_WITH_PROFILE = 0x00000001;
        private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        private const int SW_HIDE = 0;

        private enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIconOrMonitor;
            public IntPtr hProcess;
        }

        // ── advapi32.dll ──────────────────────────────────────────────────────────────
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool RevertToSelf();

        // ── shell32.dll ───────────────────────────────────────────────────────────────
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            string lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        // ── kernel32.dll ─────────────────────────────────────────────────────────────
        [DllImport("kernel32.dll")]                          // ← was wtsapi32 — WRONG
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ── wtsapi32.dll ─────────────────────────────────────────────────────────────
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern void WTSFreeMemory(IntPtr memory);

        // ── userenv.dll ──────────────────────────────────────────────────────────────
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment,
            IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        // ── advapi32.dll ─────────────────────────────────────────────────────────────
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr hToken,
            string? lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        // ── Constants ─────────────────────────────────────────────────────────────────
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;



        

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);


        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        //private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        //private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;

        /// <summary>
        /// Launch executable in active user session
        /// </summary>
        /// 


        

        
        public static ProcessResult LaunchInUserSession(
    string exePath,
    string arguments,
    bool waitForExit = true,
    int timeoutMs = 30000)
        {
            var result = new ProcessResult { Success = false };
            try
            {
                // ── Approach: use schtasks to run exe as the logged-in user ──────────
                // Windows Task Scheduler runs tasks in the full interactive user session
                // with complete desktop access — no DLL or display issues.
                // This is the most reliable way to launch a desktop app from a service.

                // Get logged-in username via WTS for proper desktop access
                string username = GetWtsUsername();
                if (string.IsNullOrEmpty(username))
                {
                    result.ErrorMessage = "Could not determine logged-in username";
                    return result;
                }

                // Unique task name per capture request
                string taskName = $"BLTCapture_{Guid.NewGuid():N}";

                // Full command — wrap in quotes
                string command = $"\"{exePath}\" {arguments}";

                // Create a one-time scheduled task that runs immediately
                // /ST with time 1 minute ahead avoids "time in past" warning
                // /RU SYSTEM runs as LocalSystem — has full desktop access via session binding
                // /IT = interactive (allows desktop access)
                //string startTime = DateTime.Now.AddMinutes(1).ToString("HH:mm");
                //string createArgs = $"/Create /TN \"{taskName}\" /TR \"{command}\" " +
                //                    $"/SC ONCE /ST {startTime} /RU \\"{ username}\\" /IT /F /RL HIGHEST";

                string startTime = DateTime.Now.AddMinutes(1).ToString("HH:mm");
                string createArgs = $"/Create /TN \"{taskName}\" /TR \"{command}\" " +
                                    $"/SC ONCE /ST {startTime} /RU \"{username}\" /IT /F /RL HIGHEST";

                var createResult = RunProcess("schtasks.exe", createArgs, 10000);
                if (!createResult.Success)
                {
                    result.ErrorMessage = $"Failed to create scheduled task: {createResult.ErrorMessage}";
                    return result;
                }

                // Run the task immediately
                var runResult = RunProcess("schtasks.exe", $"/Run /TN \"{taskName}\"", 5000);
                if (!runResult.Success)
                {
                    RunProcess("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", 5000);
                    result.ErrorMessage = $"Failed to run scheduled task: {runResult.ErrorMessage}";
                    return result;
                }

                if (waitForExit)
                {
                    // Poll task status until complete or timeout
                    var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                    bool completed = false;

                    while (DateTime.Now < deadline)
                    {
                        System.Threading.Thread.Sleep(500);

                        var queryResult = RunProcessWithOutput(
                            "schtasks.exe", $"/Query /TN \"{taskName}\" /FO CSV /NH", 5000);

                        if (queryResult.Output.Contains("Ready") ||
                            queryResult.Output.Contains("Disabled"))
                        {
                            completed = true;
                            break;
                        }
                    }

                    // Clean up task regardless
                    RunProcess("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", 5000);

                    if (!completed)
                    {
                        result.ErrorMessage = $"Capture task timed out after {timeoutMs}ms";
                        return result;
                    }
                }
                else
                {
                    // Fire and forget — clean up after a delay via background thread
                    System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ =>
                        RunProcess("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", 5000));
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Exception: {ex.Message}";
            }

            return result;
        }

        // ── Helper: get username of active console session via WTS API ───────────────
        private static string GetWtsUsername()
        {
            try
            {
                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF) return string.Empty;

                // WTSQuerySessionInformation with WTSUserName (5) + WTSDomainName (7)
                if (WTSQuerySessionInformation(
                    IntPtr.Zero, sessionId, 5 /*WTSUserName*/,
                    out IntPtr userPtr, out uint _))
                {
                    string user = Marshal.PtrToStringUni(userPtr) ?? string.Empty;
                    WTSFreeMemory(userPtr);

                    if (WTSQuerySessionInformation(
                        IntPtr.Zero, sessionId, 7 /*WTSDomainName*/,
                        out IntPtr domainPtr, out uint _))
                    {
                        string domain = Marshal.PtrToStringUni(domainPtr) ?? string.Empty;
                        WTSFreeMemory(domainPtr);

                        if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(user))
                            return $"{domain}\\{user}";
                        return user;
                    }
                    return user;
                }
            }
            catch (Exception ex)
            {
                // fallback
                System.Diagnostics.Debug.WriteLine($"GetWtsUsername failed: {ex.Message}");
            }
            return string.Empty;
        }

        // ── Helper: get currently logged-in interactive user ─────────────────────────
        private static string GetLoggedInUsername()
        {
            try
            {
                // Query session info for active console session
                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF) return string.Empty;

                // Use quser or query session to get username
                var result = RunProcessWithOutput("query.exe",
                    $"session {sessionId}", 5000);

                // Parse the output — look for Active session
                foreach (var line in result.Output.Split('\n'))
                {
                    if (line.Contains("Active") || line.Contains("Actif"))
                    {
                        var parts = line.Trim().Split(
                            new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            var name = parts[0].TrimStart('>');
                            if (!string.IsNullOrEmpty(name) && name != "SESSIONNAME")
                                return name;
                        }
                    }
                }

                // Fallback — use environment
                return System.Environment.UserName;
            }
            catch
            {
                return System.Environment.UserName;
            }
        }

        // ── Helper: run a process and return success/fail ────────────────────────────
        private static (bool Success, string ErrorMessage) RunProcess(
            string exe, string args, int timeoutMs)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                proc.Start();
                proc.WaitForExit(timeoutMs);
                return (proc.ExitCode == 0,
                        proc.ExitCode != 0 ? proc.StandardError.ReadToEnd() : string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ── Helper: run a process and capture output ─────────────────────────────────
        private static (bool Success, string Output, string ErrorMessage) RunProcessWithOutput(
            string exe, string args, int timeoutMs)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(timeoutMs);
                return (proc.ExitCode == 0, output, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
        }

        

        public class ProcessResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string? ErrorMessage { get; set; }
        }
    }
}