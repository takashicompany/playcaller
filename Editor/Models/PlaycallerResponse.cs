using Newtonsoft.Json;

namespace Playcaller.Editor.Models
{
	public static class PlaycallerResponse
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
