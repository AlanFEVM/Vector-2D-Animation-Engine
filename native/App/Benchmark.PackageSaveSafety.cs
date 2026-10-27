namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunPackageSaveFailureRecoveryRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("package-save-failure-recovery");
        string? workspace = null;
        try
        {
            var imagePath = Path.Combine(temporaryRoot, "swatch.png");
            var svgPath = Path.Combine(temporaryRoot, "logo.svg");
            var packagePath = Path.Combine(temporaryRoot, "Recovery.v2d");
            WriteFixturePng(imagePath, 32, 24);
            File.WriteAllText(svgPath,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 12\"><rect width=\"16\" height=\"12\"/></svg>");
            var imageHash = BitmapImageRasterizer.ComputeSha256(imagePath);
            var svgHash = BitmapImageRasterizer.ComputeSha256(svgPath);
            var project = VectorProject.CreateEmpty();
            AssertTimeline(project.TryAddImageAssetFromSource(
                    "Swatch", imagePath, imageHash, 32, 24, 96f,
                    BitmapImageImportSettings.Default, out _),
                "The package-save recovery image fixture could not be imported.");
            AssertTimeline(project.TryAddExternalSvgAsset("Logo", svgPath, "", svgHash, out _),
                "The package-save recovery SVG fixture could not be imported.");

            var manifestPath = ProjectPackageStore.SavePackage(project, packagePath);
            workspace = Path.GetDirectoryName(manifestPath)!;
            var originalPackage = File.ReadAllBytes(packagePath);
            var originalName = project.Name;
            project.Name = "Recovered package edits";
            var image = project.ImageAssets.Single();
            var svg = project.ExternalSvgAssets.Single();
            svg.ProjectRelativePath = "";
            svg.LastKnownSha256 = "";
            var originalImageBinding = (image.SourcePath, image.ProjectRelativePath, image.Sha256);
            var originalSvgBinding = (svg.SourcePath, svg.ProjectRelativePath, svg.LastKnownSha256);

            var saveFailed = false;
            using (var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    ProjectPackageStore.SavePackage(project, packagePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    saveFailed = true;
                }
            }

            AssertTimeline(saveFailed, "Saving a locked package did not reject the final replacement.");
            AssertTimeline(originalPackage.AsSpan().SequenceEqual(File.ReadAllBytes(packagePath)),
                "A failed package replacement changed the previous container.");
            AssertTimeline((image.SourcePath, image.ProjectRelativePath, image.Sha256) == originalImageBinding
                && (svg.SourcePath, svg.ProjectRelativePath, svg.LastKnownSha256) == originalSvgBinding,
                "A failed package save did not restore the live resource bindings and SVG checksum.");
            AssertTimeline(File.Exists(image.SourcePath) && File.Exists(svg.SourcePath)
                && BitmapImageRasterizer.ComputeSha256(image.SourcePath) == imageHash
                && BitmapImageRasterizer.ComputeSha256(svg.SourcePath) == svgHash
                && !Directory.Exists(workspace + ".new"),
                "A failed package save left live resources missing or bound to the discarded workspace.");
            var diskProject = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(diskProject.Name == originalName && project.Name == "Recovered package edits",
                "A failed package save changed the previous workspace or discarded the in-memory edits.");

            var retriedManifest = ProjectPackageStore.SavePackage(project, packagePath);
            AssertTimeline(retriedManifest == manifestPath
                && image.SourcePath == Path.Combine(workspace, image.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))
                && svg.SourcePath == Path.Combine(workspace, svg.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))
                && svg.ProjectRelativePath == $".Vault/ExternalSvg/{svg.Id}.svg"
                && svg.LastKnownSha256 == svgHash
                && File.Exists(image.SourcePath) && File.Exists(svg.SourcePath),
                "Retrying a failed package save did not bind the live assets to the installed workspace.");
            var (restored, restoredManifest) = ProjectPackageStore.OpenPackage(packagePath);
            var restoredImage = restored.ImageAssets.Single();
            var restoredSvg = restored.ExternalSvgAssets.Single();
            AssertTimeline(restoredManifest == manifestPath && restored.Id == project.Id
                && restored.Name == project.Name && restoredImage.Id == image.Id && restoredSvg.Id == svg.Id
                && restoredImage.Sha256 == imageHash && restoredSvg.LastKnownSha256 == svgHash
                && BitmapImageRasterizer.ComputeSha256(restoredImage.SourcePath) == imageHash
                && BitmapImageRasterizer.ComputeSha256(restoredSvg.SourcePath) == svgHash,
                "A package saved after releasing the replacement lock did not reload its edits and resources.");
        }
        finally
        {
            if (workspace is not null)
            {
                DeleteTemporaryDirectory(workspace + ".new");
                DeleteTemporaryDirectory(workspace + ".old");
                DeleteTemporaryDirectory(workspace);
            }
            DeleteTemporaryDirectory(temporaryRoot);
        }

        Console.WriteLine("package_save_failure_recovery_regression=ok");
    }
}
