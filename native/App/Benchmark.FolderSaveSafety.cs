namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFolderSaveAsFailureRecoveryRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("folder-save-failure-regression");
        string? workspace = null;
        try
        {
            var packagePath = Path.Combine(temporaryRoot, "Original.v2d");
            var project = VectorProject.CreateEmpty();
            var manifestPath = ProjectPackageStore.SavePackage(project, packagePath);
            workspace = Path.GetDirectoryName(manifestPath);
            using var form = new MainForm();
            RequireMethod(typeof(MainForm), "LoadProjectDocument").Invoke(form, [project, manifestPath]);
            var packageField = RequireField(typeof(MainForm), "_projectPackagePath");
            var manifestField = RequireField(typeof(MainForm), "_projectManifestPath");
            var dirtyField = RequireField(typeof(MainForm), "_projectDirty");
            packageField.SetValue(form, packagePath);
            project.Name = "Unsaved edits after failed Save As";
            RequireMethod(typeof(MainForm), "MarkProjectDirty").Invoke(form, null);
            var blocker = Path.Combine(temporaryRoot, "NotADirectory");
            File.WriteAllText(blocker, "block directory creation");
            var dialogsDismissed = 0;
            using var dialogTimer = new System.Windows.Forms.Timer { Interval = 30 };
            dialogTimer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<ModernMessageDialog>().FirstOrDefault();
                if (dialog is null) return;
                dialogsDismissed++;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            dialogTimer.Start();
            var saved = RequireMethod(typeof(MainForm), "SaveFolderProjectAs").Invoke(
                form, [Path.Combine(blocker, "Failed.v2dProject")]) is true;
            AssertTimeline(
                !saved && dialogsDismissed == 1
                && Equals(packageField.GetValue(form), packagePath)
                && Equals(manifestField.GetValue(form), manifestPath)
                && dirtyField.GetValue(form) is true,
                "Failed folder Save As changed the active package, manifest, or dirty state.");
            AssertTimeline(RequireMethod(typeof(MainForm), "SaveProject").Invoke(form, null) is true,
                "Saving after failed folder Save As did not retry the active package.");
            dialogTimer.Stop();
            var reopened = ProjectPackageStore.OpenPackage(packagePath).Project;
            AssertTimeline(reopened.Name == project.Name && dirtyField.GetValue(form) is false,
                "Saving after failed folder Save As did not persist the current edits to the original package.");
            Console.WriteLine("folder_save_as_failure_preserves_package_dirty_retry=ok");
        }
        finally
        {
            if (workspace is not null) DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }
}
