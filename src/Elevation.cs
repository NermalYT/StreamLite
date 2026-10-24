using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace StreamLite
{
    /// <summary>
    /// "Run as administrator" without a UAC prompt on every launch: a scheduled task with highest
    /// privileges starts the app elevated. Normal launches (desktop icon, the Run key that Task Manager
    /// lists) hand off to that task. Creating or removing the task asks for admin once.
    /// </summary>
    internal static class Elevation
    {
        public const string TaskName = "StreamLite (Administrator)";
        static string PendingFile { get { return Path.Combine(AppSettings.Folder, "pending-launch.txt"); } }

        public static bool IsElevated
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        static int Schtasks(string args, bool elevate)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
            if (elevate && !IsElevated) { psi.Verb = "runas"; psi.UseShellExecute = true; }
            else psi.UseShellExecute = false;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(30000);
                return p.ExitCode;
            }
        }

        public static bool TaskExists() { return TaskExists(TaskName); }

        public static bool TaskExists(string name)
        {
            try { return Schtasks("/Query /TN \"" + name + "\"", false) == 0; }
            catch { return false; }
        }

        /// <summary>Removes a task by name (no prompt when already elevated).</summary>
        public static void DeleteTask(string name)
        {
            try { if (TaskExists(name)) Schtasks("/Delete /TN \"" + name + "\" /F", true); } catch { }
        }

        /// <summary>Starts the elevated copy through the task, passing "minimized"/"show".</summary>
        public static bool RunTask(string name, bool minimized)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(PendingFile, minimized ? "minimized" : "show");
                return Schtasks("/Run /TN \"" + name + "\"", false) == 0;
            }
            catch { return false; }
        }

        /// <summary>Creates the elevated task. Returns null on success, or an error message.</summary>
        public static string CreateTask(string exePath = null)
        {
            string sid;
            using (var id = WindowsIdentity.GetCurrent()) sid = id.User.Value;
            string exe = exePath ?? Application.ExecutablePath;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Starts StreamLite with administrator rights without a UAC prompt.</Description></RegistrationInfo>\r\n" +
                "  <Triggers />\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>4</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--from-task</Arguments>" +
                "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string path = Path.Combine(Path.GetTempPath(), "StreamLite-task.xml");
            try
            {
                File.WriteAllText(path, xml, Encoding.Unicode);
                int code = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + path + "\" /F", true);
                return code == 0 ? null : "Windows couldn't create the startup task (code " + code + ").";
            }
            catch (Win32Exception ex) { return ex.NativeErrorCode == 1223 ? "Administrator permission was declined." : ex.Message; }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(path); } catch { } }
        }

        public static string DeleteTask()
        {
            try
            {
                if (!TaskExists()) return null;
                int code = Schtasks("/Delete /TN \"" + TaskName + "\" /F", true);
                return code == 0 ? null : "Windows couldn't remove the startup task (code " + code + ").";
            }
            catch (Win32Exception ex) { return ex.NativeErrorCode == 1223 ? "Administrator permission was declined." : ex.Message; }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Starts the elevated copy. "minimized" or "show" is passed through a small file because task arguments are fixed.</summary>
        public static bool RunTask(bool minimized)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(PendingFile, minimized ? "minimized" : "show");
                return Schtasks("/Run /TN \"" + TaskName + "\"", false) == 0;
            }
            catch { return false; }
        }

        /// <summary>Restarts the app elevated after this process has exited.</summary>
        public static void RestartElevatedAfterExit(bool minimized = false)
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(PendingFile, minimized ? "minimized" : "show");
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & schtasks /Run /TN \"" + TaskName + "\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false
            };
            Process.Start(psi);
        }

        /// <summary>For a task launch: true = start hidden in the tray.</summary>
        public static bool TakePendingMinimized()
        {
            try
            {
                if (!File.Exists(PendingFile)) return false;
                string s = File.ReadAllText(PendingFile).Trim();
                File.Delete(PendingFile);
                return s == "minimized";
            }
            catch { return false; }
        }
    }
}
