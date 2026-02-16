using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayCaller.Editor.Models
{
	public class PlayCallerCommand
	{
		[JsonProperty("id")]
		public string Id { get; set; }

		[JsonProperty("type")]
		public string Type { get; set; }

		[JsonProperty("params")]
		public JObject Params { get; set; }
	}
}
