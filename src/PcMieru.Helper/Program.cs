using System.Text.Json;
using PcMieru.Helper;
using PcMieru.Wire;

return HelperMain.Run(args);

namespace PcMieru.Helper
{
    /// <summary>
    /// Entry point of the elevated Helper. The request was fixed on the command line at ShellExecute time;
    /// nothing is read from stdin, the network or a config file. Exit code = <see cref="OperationCode"/>.
    /// Owner D: replace the handlers in <see cref="Handlers"/>; keep the table closed (no handler = refused).
    /// </summary>
    internal static class HelperMain
    {
        public static int Run(string[] args)
        {
            if (!HelperRequest.TryParse(args, out var request, out var error) || request is null)
            {
                Console.Error.WriteLine(error);
                return (int)OperationCode.InvalidArguments;
            }

            HelperResult result;
            try
            {
                result = Handlers.Dispatch(request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, OperationCode.Error, ex.GetType().Name);
            }

            WriteResult(request, result);
            return (int)result.Code;
        }

        /// <summary>The caller pre-creates the file in its own folder; the Helper only fills it and never creates paths.</summary>
        private static void WriteResult(HelperRequest request, HelperResult result)
        {
            try
            {
                if (!File.Exists(request.ResultPath))
                {
                    return;
                }

                using var stream = new FileStream(request.ResultPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, result, WireJsonContext.Default.HelperResult);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class Handlers
    {
        public static HelperResult Dispatch(HelperRequest request)
        {
            var name = HelperOperations.Get(request.Op).Name;
            return request.Op switch
            {
                HelperOperation.ServiceStart
                    or HelperOperation.ServiceStop
                    or HelperOperation.ServiceRestart
                    or HelperOperation.StartupHklmSet
                    or HelperOperation.ProcessTerminate
                    or HelperOperation.ProcessPriority
                    or HelperOperation.ProcessEfficiency
                    or HelperOperation.SmartRead
                    or HelperOperation.NvmeHealth => NotImplemented(request, name),
                _ => new HelperResult(request.Nonce, name, OperationCode.Unsupported, "operation not in the table"),
            };
        }

        private static HelperResult NotImplemented(HelperRequest request, string name) =>
            new(request.Nonce, name, OperationCode.NotImplemented, "skeleton: nothing was done");
    }
}
