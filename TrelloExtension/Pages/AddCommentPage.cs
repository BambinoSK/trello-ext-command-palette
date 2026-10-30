using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TrelloExtension.Models;
using TrelloExtension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// ContentPage that hosts an Adaptive Card form for adding a comment to a Trello card.
/// Trello API: POST /1/cards/{cardId}/actions/comments?text={comment}
/// </summary>
internal sealed partial class AddCommentPage : ContentPage
{
    private readonly AddCommentForm _form;

    public AddCommentPage(TrelloService trello, TrelloCard card, Action? onCompleted = null)
    {
        _form = new AddCommentForm(trello, card, onCompleted);
        Icon = new IconInfo("\uE90A"); // Comment
        Title = "Add comment";
        Name = "Add comment";
    }

    public override IContent[] GetContent() => [_form];

    // ── Inner FormContent ────────────────────────────────────────────────────

    /// <summary>
    /// Adaptive Card form with a multiline text input for the comment body.
    /// On submit, POSTs the comment to Trello and navigates back.
    /// </summary>
    private sealed partial class AddCommentForm : FormContent
    {
        private readonly TrelloService _trello;
        private readonly TrelloCard _card;
        private readonly Action? _onCompleted;

        public AddCommentForm(TrelloService trello, TrelloCard card, Action? onCompleted)
        {
            _trello = trello;
            _card = card;
            _onCompleted = onCompleted;

            TemplateJson = """
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.6",
                "body": [
                    {
                        "type": "TextBlock",
                        "size": "medium",
                        "weight": "bolder",
                        "text": "Add comment to \"${title}\"",
                        "horizontalAlignment": "center",
                        "wrap": true,
                        "style": "heading"
                    },
                    {
                        "type": "Input.Text",
                        "label": "Comment",
                        "style": "text",
                        "id": "CommentText",
                        "isRequired": true,
                        "errorMessage": "Comment cannot be empty",
                        "placeholder": "Type your comment…",
                        "isMultiline": true
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Add comment",
                        "data": {
                            "id": "AddComment"
                        }
                    }
                ]
            }
            """;

            DataJson = new JsonObject { ["title"] = card.Name }.ToJsonString();
        }

        public override CommandResult SubmitForm(string payload)
        {
            var formInput = JsonNode.Parse(payload)?.AsObject();
            var comment = formInput?["CommentText"]?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(comment))
                return CommandResult.KeepOpen();

            _ = Task.Run(async () =>
            {
                try
                {
                    await _trello.AddCommentAsync(_card.Id, comment, CancellationToken.None).ConfigureAwait(false);
                    _onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    var toast = new ToastStatusMessage($"Failed to add comment: {ex.Message}");
                    toast.Show();
                }
            });

            return CommandResult.GoBack();
        }
    }
}
