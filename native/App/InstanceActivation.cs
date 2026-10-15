using System.IO.Pipes;
using System.Text;

namespace VectorAnimationEngine;

/// <summary>
/// 单实例下的文件激活通道：已运行实例启动本地命名管道服务；用户再次双击 .v2d 工程
/// （启动第二个进程且拿不到单实例锁）时，新进程把工程路径转发给已运行实例打开。
/// </summary>
internal static class InstanceActivation
{
    private const string PipeName = "Vector2DAnimationEngine.Native.Activation";
    private const int ConnectTimeoutMilliseconds = 2000;
    private const int MaxPathBytes = 32 * 1024;

    internal static void StartServer(Action<string> onProjectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onProjectPath);
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var path = await ReadProjectPathAsync(server).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        try { onProjectPath(path); }
                        catch (Exception exception) { AppLog.Warn("Activation handler failed: " + exception.Message); }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    AppLog.Warn("Instance activation pipe loop restarted after an error: " + exception.Message);
                    try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, cancellationToken);
    }

    internal static bool Forward(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMilliseconds);
            var payload = Encoding.UTF8.GetBytes(projectPath);
            if (payload.Length > MaxPathBytes) return false;
            var lengthBytes = BitConverter.GetBytes(payload.Length);
            client.Write(lengthBytes, 0, lengthBytes.Length);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            AppLog.Info($"Forwarded activation request for '{projectPath}' to the running instance.");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Could not forward the activation request to the running instance: " + exception.Message);
            return false;
        }
    }

    private static async Task<string?> ReadProjectPathAsync(NamedPipeServerStream server)
    {
        var lengthBuffer = await ReadExactAsync(server, 4).ConfigureAwait(false);
        if (lengthBuffer is null) return null;
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > MaxPathBytes) return null;
        var payload = await ReadExactAsync(server, length).ConfigureAwait(false);
        return payload is null ? null : Encoding.UTF8.GetString(payload);
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total)).ConfigureAwait(false);
            if (read == 0) return null;
            total += read;
        }

        return buffer;
    }
}
