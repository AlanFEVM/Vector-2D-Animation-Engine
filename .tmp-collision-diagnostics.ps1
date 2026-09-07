$asm = [Reflection.Assembly]::LoadFrom([string](Join-Path (Get-Location) 'native\bin\Release\net8.0-windows\VectorAnimationEngine.dll'))
$flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$store = $asm.GetType('VectorAnimationEngine.ProjectVaultStore', $true)
$load = $store.GetMethod('Load', $flags)
$manifest = [string](Join-Path (Get-Location) '破碎测试\Untitled Project.v2dProject')
$project = $load.Invoke($null, [object[]]@($manifest))
$objects = $project.DrawingObjects
$scene = $objects[0].Scene
"drawingObjects=$($objects.Count)"
"layers=$($scene.LayerCount) objects=$($scene.ObjectCount) frames=$($scene.FrameCount) atoms=$($scene.VirtualAtomCount)"
"hasLayerEffects=$($scene.HasLayerEffects) hasDisplayLayerEffects=$($scene.HasDisplayLayerEffects) hasOutline=$($scene.HasLayerOutline)"
"hasNonNormalBlendModes=$($scene.HasNonNormalLayerBlendModes)"
@($scene.LayerKinds) | Group-Object | Sort-Object Name | ForEach-Object { "layerKind=$($_.Name) count=$($_.Count)" }
foreach ($frame in 0, 1, 2, 10, 20, 40, 80, 120, 180, 239) {
    $activeMethod = $scene.GetType().GetMethod('IsObjectActive', $flags)
    $active = 0
    for ($i = 0; $i -lt $scene.ObjectCount; $i++) {
        if ($activeMethod.Invoke($scene, [object[]]@($i, $frame))) { $active++ }
    }
    "motion_frame=$frame active=$active"
}
$bufferType = $asm.GetType('VectorAnimationEngine.SceneRenderOrderBuffer', $true)
$ctor = $bufferType.GetConstructor($flags, $null, [Type[]]@(), $null)
$buffer = $ctor.Invoke([object[]]@())
$collect = $bufferType.GetMethod('Collect', $flags)
$bounds = New-Object System.Drawing.RectangleF(-2000, -1500, 4000, 3000)
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($frame = 0; $frame -lt 24; $frame++) {
    try {
        [void]$collect.Invoke($buffer, [object[]]@($scene, $bounds, $frame))
    }
    catch {
        $_.Exception.ToString()
        if ($_.Exception.InnerException) { $_.Exception.InnerException.ToString() }
        break
    }
}
$sw.Stop()
"collect_24_total_ms=$($sw.Elapsed.TotalMilliseconds.ToString('0.000'))"
"collect_avg_ms=$(($sw.Elapsed.TotalMilliseconds / 24).ToString('0.000'))"
"last_visible=$($buffer.VisibleCount) scanned=$($buffer.ScannedCount) atoms=$($buffer.VisibleAtoms) batches=$($buffer.LastCollectBatchCount)"

$sceneDefinition = $project.Scenes[0]
$sceneDefinition.Dimension = [Enum]::Parse($asm.GetType('VectorAnimationEngine.SceneDimension'), 'ThreeD')
$sceneTrack = $sceneDefinition.Timeline.Tracks[0]
[void]$sceneDefinition.Timeline.SetTrackDuration($sceneTrack.Id, $scene.FrameCount)
$addInstance = $project.GetType().GetMethods($flags) |
    Where-Object { $_.Name -eq 'TryAddSceneInstance' -and $_.GetParameters().Count -eq 5 } |
    Select-Object -First 1
$instanceArguments = [object[]]@($sceneDefinition.Id, $objects[0].Id, [System.Drawing.PointF]::Empty, [single]0, $null)
if (-not $addInstance.Invoke($project, $instanceArguments)) { throw 'Could not create diagnostic scene instance.' }
$lightKind = [Enum]::Parse($asm.GetType('VectorAnimationEngine.SceneLightKind'), 'Directional')
$addLight = $project.GetType().GetMethod('TryAddSceneLight', $flags)
$lightArguments = [object[]]@($sceneDefinition.Id, $lightKind, $null)
[void]$addLight.Invoke($project, $lightArguments)
"lightingInitialized=$($sceneDefinition.LightingInitialized) lights=$($sceneDefinition.Lights.Count)"
$builderType = $asm.GetType('VectorAnimationEngine.SceneCompositionBuilder', $true)
$build = $builderType.GetMethod('Build', $flags)
$compositionScene = [Activator]::CreateInstance($scene.GetType())
$composition = $build.Invoke($null, [object[]]@($compositionScene, $sceneDefinition, $project.DrawingObjects, 0, [decimal]30))
"compositionLayers=$($compositionScene.LayerCount) compositionObjects=$($compositionScene.ObjectCount) owners=$($composition.ObjectOwners.Count)"
$compositionMetricsProperty = $builderType.GetProperty('LastBuildMetrics', $flags)
$bucketHitsProperty = $builderType.GetProperty('LastActiveObjectBucketCacheHits', $flags)
$bucketMissesProperty = $builderType.GetProperty('LastActiveObjectBucketCacheMisses', $flags)
for ($compositionFrame = 0; $compositionFrame -lt 8; $compositionFrame++) {
    $compositionWatch = [Diagnostics.Stopwatch]::StartNew()
    [void]$build.Invoke($null, [object[]]@($compositionScene, $sceneDefinition, $project.DrawingObjects, $compositionFrame, [decimal]30))
    $compositionWatch.Stop()
    $compositionMetrics = $compositionMetricsProperty.GetValue($null)
    "composition_frame=$compositionFrame total_ms=$($compositionWatch.Elapsed.TotalMilliseconds.ToString('0.000')) bucket=$($compositionMetrics.BucketMilliseconds.ToString('0.000')) setup=$($compositionMetrics.SetupMilliseconds.ToString('0.000')) append=$($compositionMetrics.AppendMilliseconds.ToString('0.000')) finalize=$($compositionMetrics.FinalizeMilliseconds.ToString('0.000')) hits=$($bucketHitsProperty.GetValue($null)) misses=$($bucketMissesProperty.GetValue($null))"
}
$composition = $build.Invoke($null, [object[]]@($compositionScene, $sceneDefinition, $project.DrawingObjects, 0, [decimal]30))
@($compositionScene.ShapeKind) | Group-Object | Sort-Object Name | ForEach-Object { "compositionShape=$($_.Name) count=$($_.Count)" }
for ($i = [Math]::Max(0, $compositionScene.ObjectCount - 8); $i -lt $compositionScene.ObjectCount; $i++) {
    "compositionObject=obj=$i shape=$($compositionScene.ShapeKind[$i]) layer=$($compositionScene.ObjectLayer[$i]) x=$($compositionScene.X[$i]) y=$($compositionScene.Y[$i]) w=$($compositionScene.Width[$i]) h=$($compositionScene.Height[$i]) opacity=$($compositionScene.GetLayerOpacity($compositionScene.ObjectLayer[$i])) layerKind=$($compositionScene.GetLayerKind($compositionScene.ObjectLayer[$i])) name=$($compositionScene.GetLayerName($compositionScene.ObjectLayer[$i]))"
}
$hasGradient = 0
$pathPoints = 0
$pathObjects = 0
for ($i = 0; $i -lt $compositionScene.ObjectCount; $i++) {
    if ($compositionScene.HasGradient($i)) { $hasGradient++ }
    if ($compositionScene.ShapeKind[$i] -eq 'Path') {
        $pathObjects++
        $pathPoints += $compositionScene.ShapeVertexCounts[$i]
    }
}
"compositionGradients=$hasGradient pathObjects=$pathObjects pathVertices=$pathPoints"
$gradientKinds = @{}
$gradientPaths = @{}
$gradientStopCounts = @{}
for ($i = 0; $i -lt $compositionScene.ObjectCount; $i++) {
    $kind = [string]$compositionScene.GetGradientKind($i)
    if ($gradientKinds.ContainsKey($kind)) { $gradientKinds[$kind]++ } else { $gradientKinds[$kind] = 1 }
    $path = $null
    $hasPath = $compositionScene.TryGetGradientPathWorldPoints($i, [ref]$path)
    $pathKey = if ($hasPath) { 'path' } else { 'noPath' }
    if ($gradientPaths.ContainsKey($pathKey)) { $gradientPaths[$pathKey]++ } else { $gradientPaths[$pathKey] = 1 }
    $stopCount = $compositionScene.GetGradientStops($i).Length
    if ($gradientStopCounts.ContainsKey($stopCount)) { $gradientStopCounts[$stopCount]++ } else { $gradientStopCounts[$stopCount] = 1 }
}
$gradientKinds.GetEnumerator() | Sort-Object Name | ForEach-Object { "compositionGradientKind=$($_.Name) count=$($_.Value)" }
$gradientPaths.GetEnumerator() | Sort-Object Name | ForEach-Object { "compositionGradientPath=$($_.Name) count=$($_.Value)" }
$gradientStopCounts.GetEnumerator() | Sort-Object Name | ForEach-Object { "compositionGradientStops=$($_.Name) count=$($_.Value)" }
$stageType = $asm.GetType('VectorAnimationEngine.StageControl', $true)
$stageCtor = $stageType.GetConstructor($flags, $null, [Type[]]@($scene.GetType()), $null)
$stage = $stageCtor.Invoke([object[]]@($compositionScene))
$stage.Size = [System.Drawing.Size]::new(1280, 720)
$stage.BindScene($compositionScene)
$dimension = [Enum]::Parse($asm.GetType('VectorAnimationEngine.SceneDimension'), 'ThreeD')
$cameraMotion = [Enum]::Parse($asm.GetType('VectorAnimationEngine.ReferenceCameraMotion'), 'Immediate')
$setComposition = $stage.GetType().GetMethod('SetSceneCompositionResult', $flags)
[void]$setComposition.Invoke($stage, [object[]]@($composition, $compositionScene))
$stage.ConfigureReferenceView($sceneDefinition, $dimension, $cameraMotion)
$form = New-Object System.Windows.Forms.Form
$form.ShowInTaskbar = $false
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(-30000, -30000)
$form.ClientSize = New-Object System.Drawing.Size(1280, 720)
$stage.Dock = 'Fill'
$form.Controls.Add($stage)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()
$frameProperty = $stageType.GetProperty('Frame')
$stageBindingFlags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$projectionProperty = $stageType.GetProperty('EffectiveReferenceProjection', $stageBindingFlags)
$projectionBlendProperty = $stageType.GetProperty('ReferenceProjectionBlend', $stageBindingFlags)
$d2dCommandProperty = $stageType.GetProperty('LastDirect2DCommandMilliseconds', $stageBindingFlags)
$d2dPresentProperty = $stageType.GetProperty('LastDirect2DPresentMilliseconds', $stageBindingFlags)
$d2dCacheProperty = $stageType.GetProperty('LastDirect2DCacheMaintenanceMilliseconds', $stageBindingFlags)
$d2dGeometryBuildProperty = $stageType.GetProperty('LastDirect2DObjectPathGeometryCacheBuilds', $stageBindingFlags)
$d2dGeometryReuseProperty = $stageType.GetProperty('LastDirect2DObjectPathGeometryCacheReuses', $stageBindingFlags)
$d2dGradientBuildProperty = $stageType.GetProperty('LastDirect2DGradientBrushCacheBuilds', $stageBindingFlags)
$d2dGradientReuseProperty = $stageType.GetProperty('LastDirect2DGradientBrushCacheReuses', $stageBindingFlags)
$rendererField = $stageType.GetField('_direct2DRenderer', $flags)
$renderer = $rendererField.GetValue($stage)
$rendererType = $renderer.GetType()
$materialBitmapBuildProperty = $rendererType.GetProperty('LastReference3DMaterialBitmapCacheBuilds', $flags)
$materialBitmapReuseProperty = $rendererType.GetProperty('LastReference3DMaterialBitmapCacheReuses', $flags)
$materialBitmapSubmissionProperty = $rendererType.GetProperty('LastReference3DMaterialBitmapSubmissions', $flags)
$projectiveFillProperty = $rendererType.GetProperty('LastReference3DProjectiveGradientDomainFills', $flags)
$projectiveAffineProperty = $rendererType.GetProperty('LastReference3DProjectiveAffineApproximationUses', $flags)
$strokeBatchSubmissionProperty = $rendererType.GetProperty('LastReference3DStrokeBatchSubmissions', $flags)
$strokeBatchObjectProperty = $rendererType.GetProperty('LastReference3DStrokeBatchObjects', $flags)
$bakedStrokeSkipProperty = $rendererType.GetProperty('LastReference3DBakedStrokeSkips', $flags)
$bakedStrokeCheckProperty = $rendererType.GetProperty('LastReference3DBakedStrokeChecks', $flags)
$renderItemsMethod = $stageType.GetMethod('GetReference3DSceneRenderItems', $flags)
$stageStopwatch = [Diagnostics.Stopwatch]::new()
$lightingInitializedProperty = $sceneDefinition.GetType().GetProperty('LightingInitialized', $flags)
$planBuildProperty = $stageType.GetProperty('Reference3DRenderPlanBuildCount', $flags)
$opticsBuildProperty = $stageType.GetProperty('Reference3DOpticsPlanBuildCount', $flags)
"lightingInitializedReflected=$($lightingInitializedProperty.GetValue($sceneDefinition))"
"projection=$($projectionProperty.GetValue($stage)) blend=$($projectionBlendProperty.GetValue($stage))"
for ($frame = 0; $frame -lt 4; $frame++) {
    $frameProperty.SetValue($stage, $frame)
    $stageStopwatch.Restart()
    $items = $renderItemsMethod.Invoke($stage, [object[]]@())
    $stageStopwatch.Stop()
    if ($frame -eq 0) {
        @($items | ForEach-Object { $_.Kind }) | Group-Object | Sort-Object Name | ForEach-Object { "renderKind=$($_.Name) count=$($_.Count)" }
        "opticalSurfaces=$(@($items | Where-Object { $_.OpticalSurface -ne $null }).Count)"
        "vectorLighting=$(@($items | Where-Object { $_.VectorLightingArgb -ne $null }).Count)"
        "opticalGradients=$(@($items | Where-Object { $_.OpticalGradientStops -ne $null }).Count)"
        "opticalStrokes=$(@($items | Where-Object { $_.OpticalStrokeGradientStops -ne $null }).Count)"
        "localLayered=$(@($items | Where-Object { $_.LocalLightLayers -ne $null -and $_.LocalLightLayers.Length -gt 0 }).Count)"
        "shadowLayered=$(@($items | Where-Object { $_.ShadowLayers -ne $null -and $_.ShadowLayers.Length -gt 0 }).Count)"
        "fragmentClips=$(@($items | Where-Object { $_.FragmentClip -ne $null }).Count)"
        "occlusionContours=$(@($items | Where-Object { $_.OcclusionContours -ne $null }).Count)"
        "secondaryItems=$(@($items | Where-Object { $_.SecondaryObjectIndex -ge 0 }).Count)"
        "opacityNotOne=$(@($items | Where-Object { $_.MaterialOpacity -lt 0.999999 }).Count)"
        "opticalFinish=$(@($items | Where-Object { $_.OpticalResponse.ShadeArgb -ne 0 -or $_.OpticalResponse.HighlightArgb -ne 0 -or ($_.LocalLightLayers -ne $null -and $_.LocalLightLayers.Length -gt 0) -or ($_.ShadowLayers -ne $null -and $_.ShadowLayers.Length -gt 0) }).Count)"
        "opticalSurfaceContours=$(@($items | Where-Object { $_.OpticalSurfaceContours -ne $null -and $_.OpticalSurfaceContours.Length -gt 0 }).Count)"
        "validPlanes=$(@($items | Where-Object { $_.Plane.IsValid }).Count) surfacePoints=$(@($items | Where-Object { $_.HasSurfacePoint }).Count)"
        "sourceStrokeDistinct=$(@($compositionScene.Stroke | Sort-Object -Unique) -join ',')"
        "strokeSample=$(@($items | Where-Object { $_.Kind -eq 'FrontStroke' } | Select-Object -First 5 | ForEach-Object { "obj=$($_.ObjectIndex),opacity=$($_.MaterialOpacity),local=$($_.LocalLightLayers.Length),shadow=$($_.ShadowLayers.Length),shade=$($_.OpticalResponse.ShadeArgb),highlight=$($_.OpticalResponse.HighlightArgb)" }) -join ';')"
        $strokeItems = @($items | Where-Object { $_.Kind -eq 'FrontStroke' })
        "strokeSolidArgbDistinct=$(@($strokeItems | ForEach-Object { $_.SolidStrokeOpticalBaseArgb } | Sort-Object -Unique).Count)"
        "strokeGradientStopLengths=$(@($strokeItems | ForEach-Object { if ($null -eq $_.OpticalStrokeGradientStops) { -1 } else { $_.OpticalStrokeGradientStops.Length } } | Sort-Object -Unique) -join ',')"
        "strokeWidthsDistinct=$(@($strokeItems | ForEach-Object { $_.Contours[0].Points.Length } | Sort-Object -Unique) -join ',')"
        "contourPointMinMax=$((@($items | ForEach-Object { @($_.Contours | ForEach-Object { $_.Points.Length }) }) | Measure-Object -Minimum).Minimum)-$((@($items | ForEach-Object { @($_.Contours | ForEach-Object { $_.Points.Length }) }) | Measure-Object -Maximum).Maximum)"
        $meshMethod = $stageType.GetMethods($flags) | Where-Object {
            $_.Name -eq 'TryGetReference3DProjectiveMesh' -and $_.GetParameters().Count -eq 3 -and $_.GetParameters()[1].ParameterType -eq [bool]
        } | Select-Object -First 1
        "meshMethod=$($meshMethod.ToString())"
        $meshCounts = @{}
        foreach ($item in @($items | Where-Object { $_.Kind -eq 'FrontFill' })) {
            $meshArguments = [object[]]@($item.ObjectIndex, $false, $null)
            if ($meshMethod.Invoke($stage, $meshArguments)) {
                $count = $meshArguments[2].Length
                if ($meshCounts.ContainsKey($count)) { $meshCounts[$count]++ } else { $meshCounts[$count] = 1 }
            }
        }
        $meshCounts.GetEnumerator() | Sort-Object Name | ForEach-Object { "projectiveMeshTriangles=$($_.Name) count=$($_.Value)" }
        $affineMethod = $stageType.GetMethods($flags) | Where-Object {
            $_.Name -eq 'TryGetReference3DProjectiveAffineTransform' -and $_.GetParameters().Count -eq 3
        } | Select-Object -First 1
        $affineErrors = @{}
        $affineDetails = @()
        foreach ($item in @($items | Where-Object { $_.Kind -eq 'FrontFill' })) {
            $meshArguments = [object[]]@($item.ObjectIndex, $false, $null)
            if (-not $meshMethod.Invoke($stage, $meshArguments)) { continue }
            $affineArguments = [object[]]@($meshArguments[2], $null, $null)
            $affineResult = $affineMethod.Invoke($null, $affineArguments)
            $errorKey = ([single]$affineArguments[2]).ToString('0.000')
            if ($affineErrors.ContainsKey($errorKey)) { $affineErrors[$errorKey]++ } else { $affineErrors[$errorKey] = 1 }
            if ($meshArguments[2].Length -gt 2 -or $item.ObjectIndex -ge 174) {
                $affineDetails += [pscustomobject]@{
                    obj = $item.ObjectIndex
                    shape = $compositionScene.ShapeKind[$item.ObjectIndex]
                    x = $compositionScene.X[$item.ObjectIndex]
                    y = $compositionScene.Y[$item.ObjectIndex]
                    w = $compositionScene.Width[$item.ObjectIndex]
                    h = $compositionScene.Height[$item.ObjectIndex]
                    triangles = $meshArguments[2].Length
                    affine = $affineResult
                    error = ([single]$affineArguments[2]).ToString('0.000000')
                    firstU = $meshArguments[2][0].A.U
                    firstV = $meshArguments[2][0].A.V
                    lastU = $meshArguments[2][-1].C.U
                    lastV = $meshArguments[2][-1].C.V
                }
            }
        }
        $affineErrors.GetEnumerator() | Sort-Object { [single]$_.Name } | Select-Object -First 12 | ForEach-Object { "projectiveAffineErrorPixels=$($_.Name) count=$($_.Value)" }
        $affineDetails | Sort-Object obj | Format-Table -AutoSize
        $materialCacheField = $rendererType.GetField('_reference3DMaterialBitmapCache', $flags)
        $materialCache = $materialCacheField.GetValue($renderer)
        "materialBitmapCacheEntries=$($materialCache.Count)"
        "materialBitmapDimensions=$(@($materialCache.Values | ForEach-Object { "$($_.PixelWidth)x$($_.PixelHeight)" } | Group-Object | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Count)" }) -join ',')"
        $batchMethod = $rendererType.GetMethod('CanBatchReference3DFrontStroke', $flags)
        $firstBatchFailure = $null
        $batchChecks = 0
        for ($strokeIndex = 0; $strokeIndex -lt $items.Length; $strokeIndex++) {
            $strokeItem = $items[$strokeIndex]
            if ($strokeItem.Kind -ne 'FrontStroke') { continue }
            $batchArguments = [object[]]@($stage, $strokeItem, $null, $null)
            $batchResult = $batchMethod.Invoke($null, $batchArguments)
            if ($batchChecks -lt 6) {
                "batchCheck=obj=$($strokeItem.ObjectIndex) ok=$batchResult argb=$($batchArguments[2]) width=$($batchArguments[3]) opacity=$($strokeItem.MaterialOpacity) shape=$($compositionScene.ShapeKind[$strokeItem.ObjectIndex])"
                $batchChecks++
            }
            if (-not $batchResult) {
                if ($null -eq $firstBatchFailure) { $firstBatchFailure = $strokeItem }
            }
        }
        if ($null -eq $firstBatchFailure) {
            "firstStrokeBatchFailure=none"
        } else {
            "firstStrokeBatchFailure=obj=$($firstBatchFailure.ObjectIndex) shape=$($compositionScene.ShapeKind[$firstBatchFailure.ObjectIndex]) layer=$($firstBatchFailure.LayerIndex) opacity=$($firstBatchFailure.MaterialOpacity) secondary=$($firstBatchFailure.SecondaryObjectIndex) occlusion=$($null -ne $firstBatchFailure.OcclusionContours) opticalSurface=$($null -ne $firstBatchFailure.OpticalSurface) contours=$($firstBatchFailure.Contours.Length)"
        }
        $firstStrokePosition = -1
        for ($renderIndex = 0; $renderIndex -lt $items.Length; $renderIndex++) {
            if ($items[$renderIndex].Kind -eq 'FrontStroke') { $firstStrokePosition = $renderIndex; break }
        }
        if ($firstStrokePosition -ge 0) {
            "renderOrderAroundStroke=$(@($items[($firstStrokePosition)..([Math]::Min($items.Length - 1, $firstStrokePosition + 8))] | ForEach-Object { "$($_.Kind):$($_.ObjectIndex)" }) -join ',')"
        }
    }
    "plan_frame=$frame items=$($items.Length) ms=$($stageStopwatch.Elapsed.TotalMilliseconds.ToString('0.000')) plans=$($planBuildProperty.GetValue($stage)) optics=$($opticsBuildProperty.GetValue($stage)) lightEvals=$($stage.LastReference3DOpticalLightEvaluations) shadowProjections=$($stage.LastReference3DShadowProjectionCount) shadowLayers=$($stage.LastReference3DShadowLayerCount) localLayers=$($stage.LastReference3DLocalLightLayerCount) rasterLod=$($stage.LastReference3DOpticalRasterLod)"
}
$d2dCommandTotal = 0d
$d2dPresentTotal = 0d
$d2dFrameTotal = 0d
$d2dSamples = 0
for ($frame = 20; $frame -lt 28; $frame++) {
    $frameProperty.SetValue($stage, $frame)
    $stage.Invalidate()
    $frameWatch = [Diagnostics.Stopwatch]::StartNew()
    $stage.Update()
    [System.Windows.Forms.Application]::DoEvents()
    $frameWatch.Stop()
    if ($stage.LastFrameUsedDirect2D) {
        $d2dSamples++
        $d2dCommandTotal += $d2dCommandProperty.GetValue($stage)
        $d2dPresentTotal += $d2dPresentProperty.GetValue($stage)
        $d2dFrameTotal += $frameWatch.Elapsed.TotalMilliseconds
    }
    $commandMs = [double]$d2dCommandProperty.GetValue($stage)
    $presentMs = [double]$d2dPresentProperty.GetValue($stage)
    $cacheMs = [double]$d2dCacheProperty.GetValue($stage)
    "hwnd_frame=$frame direct2d=$($stage.LastFrameUsedDirect2D) frame_ms=$($frameWatch.Elapsed.TotalMilliseconds.ToString('0.000')) command_ms=$($commandMs.ToString('0.000')) present_ms=$($presentMs.ToString('0.000')) cache_ms=$($cacheMs.ToString('0.000')) geometry=$($d2dGeometryBuildProperty.GetValue($stage))/$($d2dGeometryReuseProperty.GetValue($stage)) localGeometry=$($stage.LastDirect2DReference3DLocalPathGeometryCacheBuilds)/$($stage.LastDirect2DReference3DLocalPathGeometryCacheReuses) gradients=$($d2dGradientBuildProperty.GetValue($stage))/$($d2dGradientReuseProperty.GetValue($stage)) materialBitmaps=$($materialBitmapBuildProperty.GetValue($renderer))/$($materialBitmapReuseProperty.GetValue($renderer))/$($materialBitmapSubmissionProperty.GetValue($renderer)) strokeBatch=$($strokeBatchSubmissionProperty.GetValue($renderer))/$($strokeBatchObjectProperty.GetValue($renderer)) bakedStroke=$($bakedStrokeCheckProperty.GetValue($renderer))/$($bakedStrokeSkipProperty.GetValue($renderer)) projectiveFills=$($projectiveFillProperty.GetValue($renderer)) projectiveAffine=$($projectiveAffineProperty.GetValue($renderer)) stats=$($stage.LastStats.VisibleObjects)/$($stage.LastStats.DrawnObjects)"
}
$targetField = $renderer.GetType().GetField('_target', $flags)
$target = $targetField.GetValue($renderer)
"direct2dTargetType=$($target.GetType().FullName)"
$target.GetType().GetMethods($flags) | Where-Object { $_.Name -match 'CreateCompatibleRenderTarget|CreateLinearGradientBrush|CreateSolidColorBrush|BeginDraw|EndDraw|FillGeometry|DrawGeometry|DrawBitmap' } | Sort-Object Name, ToString | ForEach-Object { "targetMethod=$($_.ToString())" }
if ($d2dSamples -gt 0) {
    "hwnd_direct2d_avg_frame_ms=$(($d2dFrameTotal / $d2dSamples).ToString('0.000'))"
    "hwnd_direct2d_avg_command_ms=$(($d2dCommandTotal / $d2dSamples).ToString('0.000'))"
    "hwnd_direct2d_avg_present_ms=$(($d2dPresentTotal / $d2dSamples).ToString('0.000'))"
}
$materialBakedStrokeCount = @($materialCache.Values | Where-Object { $_.BakesStroke }).Count
"materialBakedStrokeEntries=$materialBakedStrokeCount"
$sampleBaked = @($materialCache.Values | Where-Object { $_.BakesStroke } | Select-Object -First 1)[0]
$sampleStrokeItem = @($items | Where-Object { $_.Kind -eq 'FrontStroke' -and $_.ObjectIndex -eq 176 })[0]
if ($null -ne $sampleBaked -and $null -ne $sampleStrokeItem) {
    "materialBakedSample=argb=$($sampleBaked.StrokeArgb),width=$($sampleBaked.StrokeWidth),itemArgb=$($sampleStrokeItem.SolidStrokeOpticalBaseArgb),itemOpacity=$($sampleStrokeItem.MaterialOpacity)"
}
$baseItemsMethod = $stageType.GetMethod('BuildReference3DSceneRenderItems', $flags)
$applyOpticsMethod = $stageType.GetMethod('ApplyReference3DOptics', $flags)
$applyOpticsMethod.GetParameters() | ForEach-Object { "apply_param=$($_.ParameterType.FullName)" }
$frameProperty.SetValue($stage, 12)
$stageStopwatch.Restart()
$baseItems = $baseItemsMethod.Invoke($stage, [object[]]@($false))
$stageStopwatch.Stop()
"profile_base_items=$($baseItems.Length) ms=$($stageStopwatch.Elapsed.TotalMilliseconds.ToString('0.000'))"
$stageStopwatch.Restart()
$profileOptics = $applyOpticsMethod.Invoke($stage, [object[]]@(,$baseItems))
$stageStopwatch.Stop()
"profile_optics_items=$($profileOptics.Length) ms=$($stageStopwatch.Elapsed.TotalMilliseconds.ToString('0.000'))"
$drawGdiMethod = $stageType.GetMethod('DrawGdi', $flags)
$renderWatch = [Diagnostics.Stopwatch]::new()
$renderWatch.Start()
for ($frame = 20; $frame -lt 26; $frame++) {
    $frameProperty.SetValue($stage, $frame)
    $bitmap = New-Object System.Drawing.Bitmap(1280, 720)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        [void]$drawGdiMethod.Invoke($stage, [object[]]@($graphics))
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
$renderWatch.Stop()
"gdi_6_frames_ms=$($renderWatch.Elapsed.TotalMilliseconds.ToString('0.000'))"
"gdi_avg_ms=$(($renderWatch.Elapsed.TotalMilliseconds / 6).ToString('0.000'))"
$stage.Dispose()
$form.Dispose()
