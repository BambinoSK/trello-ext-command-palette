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
/// ContentPage that hosts an Adaptive Card form for editing an existing comment.
/// Trello API: PUT /1/actions/{actionId}?text={newText}
/// </summary>
internal sealed partial class EditCommentPage : ContentPage
{
    private readonly EditCommentForm _form;

    public EditCommentPage(TrelloService trello, TrelloComment comment, string cardName, Action? onCompleted = null)
    {
        _form = new EditCommentForm(trello, comment, cardName, onCompleted);
        Icon = new IconInfo("\uE8AC"); // Rename
        Title = "Edit comment";
        Name = "Edit comment";
    }

    public override IContent[] GetContent() => [_form];

    // ── Inner FormContent ────────────────────────────────────────────────────

    /// <summary>
    /// Adaptive Card form with a multiline text input pre-filled with the current
    /// comment text. On submit, PUTs the new text to Trello and navigates back.
    /// </summary>
    private sealed partial class EditCommentForm : FormContent
    {
        private readonly TrelloService _trello;
        private readonly TrelloComment _comment;
        private readonly Action? _onCompleted;

        public EditCommentForm(TrelloService trello, TrelloComment comment, string cardName, Action? onCompleted)
        {
            _trello = trello;
            _comment = comment;
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
                        "text": "Edit comment on \"${card}\"",
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
                        "value": "${currentText}",
                        "isMultiline": true,
                        "maxLength": 16384
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Save",
                        "data": {
                            "id": "EditComment"
                        }
                    }
                ]
            }
            """;

            DataJson = new JsonObject { ["card"] = cardName, ["currentText"] = comment.Text }.ToJsonString();
        }

        public override CommandResult SubmitForm(string payload)
        {
            // Verified from SampleContentForm.SubmitForm: JsonNode.Parse(payload)?.AsObject()
            var formInput = JsonNode.Parse(payload)?.AsObject();
            var newText = formInput?["CommentText"]?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newText))
                return CommandResult.KeepOpen();

            // Skip the API call when nothing changed.
            if (newText == _comment.Text)
                return CommandResult.GoBack();

            _ = Task.Run(async () =>
            {
                try
                {
                    await _trello.UpdateCommentAsync(_comment.Id, newText, CancellationToken.None).ConfigureAwait(false);
                    _onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    var toast = new ToastStatusMessage($"Failed to edit comment: {ex.Message}");
                    toast.Show();
                }
            });

            return CommandResult.GoBack();
        }
    }
}
