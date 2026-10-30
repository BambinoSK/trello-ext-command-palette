using System.Collections.Generic;
using System.Text.Json.Serialization;
using TrelloExtension.Models;

namespace TrelloExtension;

/// <summary>
/// AOT-safe System.Text.Json source-generation context.
/// Required because the project uses PublishTrimmed=true and IsAotCompatible=true.
/// All types that are deserialized from Trello API responses must be registered here.
/// </summary>
[JsonSerializable(typeof(List<TrelloBoard>))]
[JsonSerializable(typeof(List<TrelloList>))]
[JsonSerializable(typeof(List<TrelloCard>))]
[JsonSerializable(typeof(List<TrelloLabel>))]
[JsonSerializable(typeof(List<TrelloChecklist>))]
[JsonSerializable(typeof(List<TrelloCheckItem>))]
[JsonSerializable(typeof(List<TrelloMember>))]
[JsonSerializable(typeof(List<TrelloComment>))]
[JsonSerializable(typeof(List<TrelloAttachment>))]
[JsonSerializable(typeof(TrelloSearchResult))]
[JsonSerializable(typeof(TrelloBoard))]
[JsonSerializable(typeof(TrelloList))]
[JsonSerializable(typeof(TrelloCard))]
[JsonSerializable(typeof(TrelloLabel))]
[JsonSerializable(typeof(TrelloChecklist))]
[JsonSerializable(typeof(TrelloCheckItem))]
[JsonSerializable(typeof(TrelloMember))]
[JsonSerializable(typeof(TrelloComment))]
[JsonSerializable(typeof(TrelloAttachment))]
[JsonSerializable(typeof(TrelloCommentData))]
[JsonSerializable(typeof(TrelloOAuthTokens))]
[JsonSerializable(typeof(TrelloOAuthTokenResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class TrelloJsonContext : JsonSerializerContext
{
}
