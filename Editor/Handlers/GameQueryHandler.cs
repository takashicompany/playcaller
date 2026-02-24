using System;
using Newtonsoft.Json.Linq;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class GameQueryHandler
	{
		public static string Handle(PlaycallerCommand command)
		{
			try
			{
				string queryType = command.Params?["queryType"]?.ToString() ?? "";

				var handler = PlaycallerGameBridge.QueryHandler;
				if (handler == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"No game bridge registered. The game has not implemented query support yet.",
						"NO_BRIDGE");
				}

				string jsonResult = handler(queryType);
				if (jsonResult == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"Game bridge returned null. Query not supported.",
						"NULL_RESPONSE");
				}

				// JSON文字列をデシリアライズしてからSuccessに渡す
				// （文字列のまま渡すとJsonConvert.SerializeObjectで二重エンコードになるため）
				var parsed = JToken.Parse(jsonResult);
				return PlaycallerResponse.Success(command.Id, parsed);
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"Game query failed: {ex.Message}", "GAME_QUERY_ERROR");
			}
		}
	}
}
