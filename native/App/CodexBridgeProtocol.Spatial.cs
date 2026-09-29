using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class CodexBridgeProtocol
{
    internal static CodexToolDefinition[] CreateSpatialTools() =>
    [
        new(
            "scene_lights_get",
            "Read the active scene lights and their evaluated settings at the current editor frame.",
            Schema("\"sceneId\":{\"type\":\"string\"}", ""),
            true),
        new(
            "scene_light_create",
            "Create a light in the active scene. The light kind is immutable after creation.",
            Schema(
                "\"sceneId\":{\"type\":\"string\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"Directional\",\"Point\",\"Area\",\"Ambient\"]}," +
                "\"name\":{\"type\":\"string\",\"maxLength\":80}," +
                "\"enabled\":{\"type\":\"boolean\"}," +
                "\"colorArgb\":{\"type\":\"integer\"}," +
                "\"intensity\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1000}," +
                "\"range\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000}," +
                "\"positionX\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"positionY\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"positionZ\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}," +
                "\"rotationX\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationY\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationZ\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000}," +
                "\"areaWidth\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000},\"areaHeight\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000}," +
                "\"castsShadows\":{\"type\":\"boolean\"}," +
                "\"shadowStrength\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"shadowSoftness\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}",
                "\"kind\""),
            false),
        new(
            "scene_light_update",
            "Update an active scene light at the editor's current frame, respecting Auto Key and the existing timeline edit policy.",
            Schema(
                "\"sceneId\":{\"type\":\"string\"},\"lightId\":{\"type\":\"string\"}," +
                "\"name\":{\"type\":\"string\",\"maxLength\":80}," +
                "\"enabled\":{\"type\":\"boolean\"},\"colorArgb\":{\"type\":\"integer\"}," +
                "\"intensity\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1000},\"range\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000}," +
                "\"positionX\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"positionY\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"positionZ\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}," +
                "\"rotationX\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationY\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationZ\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000}," +
                "\"areaWidth\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000},\"areaHeight\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000}," +
                "\"castsShadows\":{\"type\":\"boolean\"}," +
                "\"shadowStrength\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"shadowSoftness\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}",
                "\"lightId\""),
            false),
        new(
            "scene_light_remove",
            "Remove an active scene light by its stable light ID.",
            Schema("\"sceneId\":{\"type\":\"string\"},\"lightId\":{\"type\":\"string\"}", "\"lightId\""),
            false),
        new(
            "scene_instance_set_transform",
            "Set an active scene instance transform using Auto Key/exposure policy. Rotations are degrees; scaleX/Y are ratios, scaleZ is extrusion thickness in world units. Omitted fields retain their value.",
            Schema(
                "\"sceneId\":{\"type\":\"string\"},\"instanceId\":{\"type\":\"string\"}," +
                "\"x\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"y\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"z\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}," +
                "\"rotationX\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationY\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000},\"rotationZ\":{\"type\":\"number\",\"minimum\":-360000,\"maximum\":360000}," +
                "\"scaleX\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":1000},\"scaleY\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":1000},\"scaleZ\":{\"type\":\"number\",\"minimum\":0,\"maximum\":5000000}",
                "\"instanceId\""),
            false),
        new(
            "scene_camera_get",
            "Read the active scene's persisted document camera parameters.",
            Schema("\"sceneId\":{\"type\":\"string\"}", ""),
            true),
        new(
            "scene_camera_update",
            "Update the active scene's persisted document camera parameters.",
            Schema(
                "\"sceneId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"maxLength\":80}," +
                "\"projection\":{\"type\":\"string\",\"enum\":[\"Orthographic\",\"Perspective\"]}," +
                "\"x\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"y\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"z\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}," +
                "\"depth\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000},\"orthographicSize\":{\"type\":\"number\",\"minimum\":0.01,\"maximum\":5000000}," +
                "\"fieldOfViewDegrees\":{\"type\":\"number\",\"minimum\":1,\"maximum\":179}",
                ""),
            false),
        new(
            "scene_view_get",
            "Read the editor's transient reference view, separate from the persisted document camera.",
            Schema(),
            true),
        new(
            "scene_view_update",
            "Update the editor's transient reference view. Yaw and pitch are radians and require 3D; use either viewDirection or yaw/pitch. Does not modify the document camera or scene dimension.",
            Schema(
                "\"viewDirection\":{\"type\":\"string\",\"enum\":[\"Front\",\"Back\",\"Left\",\"Right\",\"Top\",\"Bottom\",\"Isometric\"]}," +
                "\"yaw\":{\"type\":\"number\",\"minimum\":-314,\"maximum\":314},\"pitch\":{\"type\":\"number\",\"minimum\":-1.5,\"maximum\":1.5},\"distance\":{\"type\":\"number\",\"minimum\":2000,\"maximum\":80000},\"zoomScale\":{\"type\":\"number\",\"minimum\":0.25,\"maximum\":8}," +
                "\"targetX\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"targetY\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"targetZ\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}",
                ""),
            false),
        new(
            "scene_set_dimension",
            "Set the persisted active scene dimension and its reference view to 2D or 3D.",
            Schema("\"sceneId\":{\"type\":\"string\"},\"dimension\":{\"type\":\"string\",\"enum\":[\"TwoD\",\"ThreeD\"]}", "\"dimension\""),
            false)
    ];
}
