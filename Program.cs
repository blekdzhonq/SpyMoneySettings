using System.Diagnostics;
using System.Reflection;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();

            // 1) Find Quantower/SpyMoney root (has a "Settings" folder)
            string? qtRoot = AutoDetectQuantowerRoot() ?? AskForRoot();
            if (qtRoot is null)
            {
                MessageBox.Show("Quantower folder not selected. Aborting.", "SpyMoney Settings Installer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 10;
            }

            // 2) Ask user to close running app to avoid file locks
            if (Process.GetProcessesByName("Quantower").Any() ||
                Process.GetProcessesByName("SpyMoney").Any())
            {
                var r = MessageBox.Show("Please close Quantower/SpyMoney, then click OK to continue.\nClick Cancel to exit.",
                    "SpyMoney Settings Installer", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) return 11;
            }

            // 3) Prepare log/backup
            string backups = Path.Combine(qtRoot, "Backups", "Settings");
            Directory.CreateDirectory(backups);
            string logFile = Path.Combine(qtRoot, "Backups",
                "settings-installer-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt");
            void Log(string m) { try { File.AppendAllText(logFile, m + Environment.NewLine); } catch { } }

            Log($"Quantower root: {qtRoot}");

            // 4) Copy embedded Settings/*
            InstallSettingsTree(qtRoot, Log);

            MessageBox.Show("Settings installed.\nRestart Quantower to load them.",
                "SpyMoney Settings Installer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Install failed:\n\n" + ex, "SpyMoney Settings Installer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return -1;
        }
    }

    // ---- Settings copier ----
    private static void InstallSettingsTree(string qtRoot, Action<string> Log)
    {
        var asm = Assembly.GetExecutingAssembly();
        var all = asm.GetManifestResourceNames();

        // Only resources that start with "Settings/" (as defined in the csproj LogicalName)
        var names = all.Where(n =>
            n.StartsWith("Settings/", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("Settings\\", StringComparison.OrdinalIgnoreCase)) // safety if a tool emitted backslashes
            .ToArray();

        if (names.Length == 0)
            throw new InvalidOperationException("No embedded Settings files found. Did you include the Settings folder in the project?");

        string dstRoot = Path.Combine(qtRoot, "Settings");
        Directory.CreateDirectory(dstRoot);

        foreach (var original in names)
        {
            // Normalize to forward slashes for path math
            string norm = original.Replace('\\', '/'); // just in case
            const string prefix = "Settings/";
            string rel = norm.Substring(prefix.Length); // e.g., "Workspaces/X.xml" or "Templates/Y.xml" or "file.json"

            string target = Path.Combine(dstRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Backup if exists
            if (File.Exists(target))
            {
                string backup = Path.Combine(qtRoot, "Backups", "Settings",
                    Path.GetFileName(target) + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak");
                File.Copy(target, backup, overwrite: true);
                Log($"BACKUP  {target} -> {backup}");
            }

            using var s = asm.GetManifestResourceStream(original)
                ?? throw new InvalidOperationException($"Resource not found: {original}");
            using var fs = File.Create(target);
            s.CopyTo(fs);

            Log($"COPY    {original} -> {target}");
        }
    }

    // ---- Root detection helpers ----
    private static string? AutoDetectQuantowerRoot()
    {
        var productNames = new[] { "Quantower", "SpyMoney" };
        var baseDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            @"C:\"
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();

        foreach (var product in productNames)
            foreach (var baseDir in baseDirs)
            {
                var candidate = Path.Combine(baseDir, product);
                if (IsQtRoot(candidate)) return candidate;
            }

        // try from running process
        try
        {
            var p = Process.GetProcesses()
                .FirstOrDefault(pr => productNames.Any(name =>
                    pr.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)));
            var exePath = p?.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var root = Path.GetDirectoryName(exePath)!;
                if (IsQtRoot(root)) return root;
                var parent = Directory.GetParent(root)?.FullName;
                if (parent is not null && IsQtRoot(parent)) return parent;
            }
        }
        catch { }

        // shallow search under user profile
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = TryFindProductRoot(home, productNames, maxDepth: 3);
        return found;
    }

    private static bool IsQtRoot(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "Settings"));

    private static string? TryFindProductRoot(string startDir, string[] products, int maxDepth)
    {
        try
        {
            if (maxDepth < 0 || string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir)) return null;

            foreach (var dir in Directory.EnumerateDirectories(startDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(dir);
                if (products.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)) && IsQtRoot(dir))
                    return dir;
            }

            if (maxDepth == 0) return null;

            foreach (var dir in Directory.EnumerateDirectories(startDir, "*", SearchOption.TopDirectoryOnly))
            {
                var hit = TryFindProductRoot(dir, products, maxDepth - 1);
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    private static string? AskForRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select your Quantower root (the folder that contains 'Settings')",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}