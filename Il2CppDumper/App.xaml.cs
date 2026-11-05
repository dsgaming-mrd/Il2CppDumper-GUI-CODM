using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Il2CppDumper
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        protected override void OnStartup(StartupEventArgs e)
        {
            AttachConsole(-1);

            if (e.Args.Length >= 2)
            {
                string binFile = e.Args[0];
                string datFile = e.Args[1];
                string outputDir = e.Args.Length > 2 ? e.Args[2] : Path.Combine(Path.GetDirectoryName(binFile)!, "dump_out");
                bool ok = ProgramRunner.RunDump(binFile, datFile, outputDir);
                Environment.Exit(ok ? 0 : 1);
                return;
            }

            base.OnStartup(e);
            var mainForm = new MainForm();
            mainForm.Show();
        }
    }
}
