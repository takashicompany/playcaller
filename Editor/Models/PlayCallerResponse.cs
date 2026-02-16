using Newtonsoft.Json;

namespace PlayCaller.Editor.Models
{
    public static class PlayCallerResponse
    {
        public static string Success(string id, object result)
        {
            return JsonConvert.SerializeObject(new
            {
                id = id,
                status = "success",
                result = result
            });
        }

        public static string Error(string id, string error, string code = null)
        {
            return JsonConvert.SerializeObject(new
            {
                id = id,
                status = "error",
                error = error,
                code = code ?? "ERROR"
            });
        }
    }
}
