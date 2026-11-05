using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Case-driven BIM Diagnosis and Repair Setup")]
[assembly: AssemblyVersion("2026.11.4.4")]

class Setup : Form {
    readonly Label status = new Label { Left=24, Top=22, Width=570, Height=115,
        Text="案例驅動 BIM 診斷與修正\r\nCase-driven BIM Diagnosis and Repair\r\n\r\nRevit 2022 + pyRevit · Windows x64\r\n請先關閉 Revit / Close Revit before installation." };
    readonly Button install = new Button { Left=400, Top=150, Width=185, Height=40, Text="安裝 / Install" };
    bool working, complete;

    Setup() {
        Text="BIM 診斷與修正 / Setup";
        ClientSize=new Size(610,215); Font=new Font("Microsoft JhengHei UI",10);
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(status); Controls.Add(install);
        FormClosing += (s,e) => { if(working) e.Cancel=true; };
        install.Click += async (s,e) => {
            if(complete) { Close(); return; }
            working=true; install.Enabled=false;
            status.Text="正在安裝及檢查，請稍候…\r\nInstalling and checking…";
            try {
                string message=await Task.Run(() => Install());
                status.Text=message; install.Text="關閉 / Close";
                complete=true;
            } catch(Exception error) {
                status.Text="安裝未完成 / Installation failed\r\n"+error.Message;
                MessageBox.Show(error.Message,"安裝未完成 / Installation failed");
            } finally { working=false; install.Enabled=true; }
        };
    }

    static void Extract(string destination) {
        destination=Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using(var archive=new ZipArchive(resource,ZipArchiveMode.Read)) {
            foreach(var item in archive.Entries) {
                string target=Path.GetFullPath(Path.Combine(destination,item.FullName));
                if(!target.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid package path.");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if(item.Name.Length>0) item.ExtractToFile(target,false);
            }
        }
    }

    static string Install() {
        string folder=Path.Combine(Path.GetTempPath(),"CaseDrivenBIM-"+Guid.NewGuid().ToString("N"));
        try {
            Extract(folder);
            var info=new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \""+
                Path.Combine(folder,"Install.ps1")+"\" -Payload \""+Path.Combine(folder,"app")+"\"");
            info.UseShellExecute=false; info.CreateNoWindow=true;
            info.RedirectStandardOutput=true; info.RedirectStandardError=true;
            info.StandardOutputEncoding=System.Text.Encoding.UTF8;
            info.StandardErrorEncoding=System.Text.Encoding.UTF8;
            using(var process=Process.Start(info)) {
                var output=process.StandardOutput.ReadToEndAsync();
                var errors=process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if(process.ExitCode!=0) throw new Exception(output.Result+errors.Result);
            }
            return "安裝及檢查完成。請開啟 Revit 2022。\r\nInstallation and checks completed. Open Revit 2022.\r\n\r\n開始功能表 / Start menu: Case-driven BIM";
        } finally {
            if(Directory.Exists(folder)) Directory.Delete(folder,true);
        }
    }

    [STAThread] static int Main(string[] args) {
        try {
            // Used by release verification; extraction alone never installs/registers.
            if(args.Length==2 && args[0]=="/extract") { Extract(args[1]); return 0; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Setup()); return 0;
        } catch(Exception error) {
            if(args.Length==0) MessageBox.Show(error.Message,"Setup failed");
            return 1;
        }
    }
}
