using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;

namespace IAR_Gen
{
    public partial class Main : Form
    {

        private class PrjGroup
        {
            internal class PrjFile
            {
                public string Name;
                public readonly List<string> Excludes;

                public PrjFile()
                {
                    Excludes = new List<string>();
                }
            }
            public string Name;
            public readonly List<PrjFile> Files;
            public List<PrjGroup> SubGroups;
            // ReSharper disable once MemberCanBePrivate.Local
            public readonly PrjGroup Parent;
            public readonly List<string> Excludes;
            public PrjGroup(ref PrjGroup parent)
            {
                Parent = parent;
                Excludes = new List<string>();
                Files = new List<PrjFile>();
                SubGroups = new List<PrjGroup>();
            }

            // ReSharper disable once MemberCanBePrivate.Local
            public string FullName
            {
                get
                {
                    if (Parent == null)
                    {
                        return Name;
                    }
                    return Parent.FullName + "/" + Name;
                }
            }
            public static List<string> ReadVpath(ref List<PrjGroup> prjgroups, string exclude, List<string> ret = null)
            {
                ret = ret ?? new List<string>();
                foreach (PrjGroup prjgroup in prjgroups)
                {
                    if (prjgroup.Excludes.Contains(exclude))
                        continue;
                    ret.Add("[\"" + prjgroup.FullName + "\"] = { \"" + string.Join("\" , \"", from file in prjgroup.Files where !file.Excludes.Contains(exclude) select file.Name) + "\" }");
                    ReadVpath(ref prjgroup.SubGroups, exclude, ret);
                }
                return ret;
            }
            public static List<string> ReadAllFile(ref List<PrjGroup> prjgroups, string exclude)
            {
                List<string> ret = new List<string>();
                foreach (var prjgroup in prjgroups)
                {
                    if (prjgroup.Excludes.Contains(exclude))
                        continue;
                    ret.AddRange(from file in prjgroup.Files where !file.Excludes.Contains(exclude) select file.Name);
                    ret.AddRange(ReadAllFile(ref prjgroup.SubGroups, exclude));
                }
                return ret;
            }
        }

        private class PrjConfig
        {
            public string Name;
            public List<string> Defines;
            public List<string> IncludePaths;
            public List<string> PreIncludes;
            // ReSharper disable once NotAccessedField.Local
            public bool Cmsis;

            public PrjConfig()
            {
                Defines = new List<string>();
                IncludePaths = new List<string>();
                PreIncludes = new List<string>();
            }
        }

        string IncOverride =
            "premake.override(premake.vstudio.vc2010, \"includePath\", function(base,cfg)\r\n" +
            "   local dirs = premake.vstudio.path(cfg, cfg.sysincludedirs)\r\n" +
            "    if #dirs > 0 then\r\n" +
            "    premake.vstudio.vc2010.element(\"IncludePath\", nil, \"%s\", table.concat(dirs, \";\"))\r\n" +
            "    end\r\n" +
            "end)";
        public Main(string path = "")
        {
            InitializeComponent();
            cmbTarget.SelectedIndex = 0;
            txtPath.Text = path;
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            var prj = new OpenFileDialog { Filter = @"IAR Project (*.ewp)|*.ewp" };
            if (prj.ShowDialog() != DialogResult.OK) return;
            txtPath.Text = prj.FileName;
        }

        private void btnGo_Click(object sender, EventArgs e)
        {
            try
            {
                string text = File.ReadAllText(txtPath.Text);
                XmlReader reader = XmlReader.Create(new StringReader(text));
                List<PrjConfig> prjConfigs = new List<PrjConfig>();
                List<PrjGroup> prjGroups = new List<PrjGroup>();
                //read configs
                reader.ReadToFollowing("configuration");
                do
                {
                    XmlReader subReader = reader.ReadSubtree();
                    subReader.ReadToDescendant("name");
                    subReader.Read();
                    prjConfigs.Add(new PrjConfig());
                    prjConfigs.Last().Name = subReader.Value;
                    // General settings
                    subReader.ReadToFollowing("settings");
                    do
                    {
                        subReader.Read();
                        if (subReader.EOF) break;
                    }
                    while (subReader.NodeType != XmlNodeType.Text || subReader.Value == "OGUseCmsis");
                    do
                    {
                        subReader.Read();
                        if (subReader.EOF) break;
                    }
                    while (subReader.NodeType != XmlNodeType.Text);
                    prjConfigs.Last().Cmsis = int.Parse(subReader.Value) > 0;
                    //C/C++ settings
                    subReader.ReadToFollowing("settings");
                    prjConfigs.Last().Defines = GetSubValue(ref subReader, "CCDefines");
                    //Add IAR define
                    prjConfigs.Last().Defines.Add("_IAR_");
                    prjConfigs.Last().Defines.Add("__ICCARM__");
                    prjConfigs.Last().Defines.Add("_Pragma(x)=");
                    prjConfigs.Last().Defines.Add("__interrupt=");
                    prjConfigs.Last().PreIncludes = GetSubValue(ref subReader, "PreInclude");
                    prjConfigs.Last().IncludePaths = GetSubValue(ref subReader, "CCIncludePath2");
                    subReader.Close();
                }
                while (reader.ReadToNextSibling("configuration"));
                //read files
                reader = XmlReader.Create(new StringReader(text));
                do
                {
                    prjGroups.Add(GetSubGroup(ref reader, null));
                }
                while (reader.ReadToNextSibling("group"));
                //
                FormatPath(ref prjGroups);
                FormatCfgPath(ref prjConfigs);
                //write configs to script
                StreamWriter file =
                    new StreamWriter(Path.GetDirectoryName(txtPath.Text) + "\\IAR_Gen.lua");
                {
                    //
                    file.WriteLine("workspace \"" + Path.GetFileNameWithoutExtension(txtPath.Text) + "\"");
                    file.WriteLine("  configurations { \"" + string.Join("\", \"", prjConfigs.Select(i => i.Name).ToArray()) + "\" }");
                    file.WriteLine("project\"" + Path.GetFileNameWithoutExtension(txtPath.Text) + "\"");
                    file.WriteLine("  kind \"ConsoleApp\"");
                    file.WriteLine("  language \"C\"");
                    foreach (var conf in prjConfigs)
                    {
                        file.WriteLine("filter \"configurations:" + conf.Name + "\"");
                        file.WriteLine("  sysincludedirs  {\"$(VC_IncludePath)\"}");
                        file.WriteLine("  defines { \"" + string.Join("\", \"", conf.Defines) + "\" }");
                        file.WriteLine("  forceincludes { \"" + string.Join("\", \"", conf.PreIncludes) + "\" }");
                        file.WriteLine("  includedirs { \"" + string.Join("\", \"", conf.IncludePaths) + "\" }");
                        var srcFiles = PrjGroup.ReadAllFile(ref prjGroups, conf.Name);
                        file.WriteLine("  files { \"" + string.Join("\", \"", srcFiles) + "\" }");
                        //file.WriteLine("  vpaths { [\"*\"] = \"..\" }");
                        var vGroups = PrjGroup.ReadVpath(ref prjGroups, conf.Name);
                        file.WriteLine("  vpaths {" + string.Join(" , ", vGroups) + " }");
                    }
                    file.Write(IncOverride);
                    file.Close();
                    Process proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "premake.exe",
                            Arguments = "--File=\"" + Path.GetDirectoryName(txtPath.Text) + "\\IAR_Gen.lua\" " + cmbTarget.Text,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    proc.Start();
                    var makeOut = proc.StandardOutput.ReadToEnd();
                    if (proc.ExitCode == 0)
                    {
                        MessageBox.Show(makeOut, @"Make output");
                        if (cmbTarget.Text.Contains("vs"))
                        {
                            DialogResult dialogResult = MessageBox.Show(@"Open Project ?", Text, MessageBoxButtons.YesNo);
                            if (dialogResult == DialogResult.Yes)
                            {
                                ProcessStartInfo psi = new ProcessStartInfo(Path.ChangeExtension(txtPath.Text, "sln"))
                                {
                                    UseShellExecute = true
                                };
                                Process.Start(psi);
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show(makeOut, @"Make output", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, @"Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void FormatCfgPath(ref List<PrjConfig> prjconfigs)
        {
            foreach (var prjconfig in prjconfigs)
            {
                prjconfig.IncludePaths = prjconfig.IncludePaths.Select(s => s.Replace("$PROJ_DIR$\\", "")).ToList();
                prjconfig.IncludePaths = prjconfig.IncludePaths.Select(s => s.Replace("$PROJ_DIR$/", "")).ToList();
                prjconfig.IncludePaths = prjconfig.IncludePaths.Select(s => s.Replace("\\", "/")).ToList();
                prjconfig.PreIncludes = prjconfig.PreIncludes.Select(s => s.Replace("$PROJ_DIR$\\", "")).ToList();
                prjconfig.PreIncludes = prjconfig.PreIncludes.Select(s => s.Replace("$PROJ_DIR$/", "")).ToList();
                prjconfig.PreIncludes = prjconfig.PreIncludes.Select(s => s.Replace("\\", "/")).ToList();
            }
        }
        void FormatPath(ref List<PrjGroup> prjgroups)
        {
            foreach (var prjgroup in prjgroups)
            {
                foreach (var file in prjgroup.Files)
                {
                    file.Name = file.Name.Replace("$PROJ_DIR$\\", "");
                    file.Name = file.Name.Replace("$PROJ_DIR$/", "");
                    file.Name = file.Name.Replace("\\", "/");
                }
                FormatPath(ref prjgroup.SubGroups);
            }
        }
        List<string> GetSubValue(ref XmlReader reader, string title)
        {
            List<string> ret = new List<string>();
            do
            {
                reader.Read();
                if (reader.EOF) return ret;
            }
            while (reader.Value != title);
            reader.Read();
            int lvl = reader.Depth;
            do
            {
                reader.Read();
                if (reader.NodeType == XmlNodeType.Text && reader.HasValue)
                {
                    ret.Add(reader.Value);
                }
            }
            while (reader.Depth >= lvl);
            reader.MoveToElement();
            return ret;
        }

        PrjGroup GetSubGroup(ref XmlReader reader, PrjGroup parent)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "group")
                reader.ReadToFollowing("group");
            var ret = new PrjGroup(ref parent);
            XmlReader subReader = reader.ReadSubtree();
            do
            {
                subReader.Read();
            }
            while (subReader.NodeType != XmlNodeType.Text);
            ret.Name = subReader.Value;
            do
            {
                if (subReader.Read() && subReader.NodeType == XmlNodeType.Element)
                {
                    switch (subReader.Name)
                    {
                    case "group":
                        ret.SubGroups.Add(GetSubGroup(ref subReader, ret));
                        break;
                    case "file":
                        var subsubReader = subReader.ReadSubtree();
                        do
                        {
                            subsubReader.Read();
                            switch (subsubReader.Name)
                            {
                            case "name":
                                subsubReader.Read();
                                if (subsubReader.NodeType == XmlNodeType.Text && subsubReader.HasValue && subsubReader.Depth == 2)
                                {
                                    ret.Files.Add(new PrjGroup.PrjFile());
                                    ret.Files.Last().Name = subsubReader.Value;
                                }
                                break;
                            case "excluded":
                                if (subsubReader.NodeType == XmlNodeType.Element)
                                {
                                    var subsubsubReader = subsubReader.ReadSubtree();
                                    do
                                    {
                                        subsubsubReader.Read();
                                        if (subsubsubReader.NodeType == XmlNodeType.Text)
                                            ret.Files.Last().Excludes.Add(subsubsubReader.Value);
                                    }
                                    while (!subsubsubReader.EOF);
                                    subsubsubReader.Close();
                                }
                                break;
                            }
                        }
                        while (!subsubReader.EOF);
                        subsubReader.Close();
                        break;
                    case "excluded":
                        do
                        {
                            subReader.Read();
                            if (subReader.NodeType == XmlNodeType.Text && subReader.HasValue)
                            {
                                ret.Excludes.Add(subReader.Value);
                            }
                        }
                        while (reader.Name != "excluded");
                        break;
                    }
                }
            }
            while (!subReader.EOF);
            subReader.Close();
            return ret;
        }
    }

}
