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
/// ContentPage that hosts an Adaptive Card form for editing a card's title and
/// description together. Replaces the separate "Change title" action.
/// Trello API: PUT /1/cards/{cardId}?name={name}&desc={desc} (single request, verified)
/// </summary>
internal sealed partial class EditCardPage : ContentPage
{
    private readonly EditCardForm _form;

    public EditCardPage(TrelloService trello, TrelloCard card, Action? onCompleted = null)
    {
        _form = new EditCardForm(trello, card, onCompleted);
        Icon = new IconInfo("\uE8AC"); // Rename
        Title = "Edit card";
        Name = "Edit card";
    }

    public override IContent[] GetContent() => [_form];

    // ── Inner FormContent ────────────────────────────────────────────────────

    /// <summary>
    /// Adaptive Card form with two inputs: Title (required, single-line prefilled
    /// with the current name) and Description (optional, multi-line prefilled with
    /// the current desc, clearable to empty - Trello descriptions are legitimately
    /// empty, so desc is deliberately NOT required). On submit, PUTs both fields
    /// to Trello in one request and navigates back.
    /// </summary>
    private sealed partial class EditCardForm : FormContent
    {
        private readonly TrelloService _trello;
        private readonly TrelloCard _card;
        private readonly Action? _onCompleted;

        public EditCardForm(TrelloService trello, TrelloCard card, Action? onCompleted)
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
                        "text": "Edit card \"${title}\"",
                        "horizontalAlignment": "center",
                        "wrap": true,
                        "style": "heading"
                    },
                    {
                        "type": "Input.Text",
                        "label": "Title",
                        "style": "text",
                        "id": "CardTitle",
                        "isRequired": true,
                        "errorMessage": "Title cannot be empty",
                        "placeholder": "Enter new card title",
                        "value": "${currentTitle}",
                        "isMultiline": true,
                        "maxLength": 16384
                    },
                    {
                        "type": "Input.Text",
                        "label": "Description",
                        "style": "text",
                        "id": "CardDescription",
                        "placeholder": "Describe this card (optional)",
                        "value": "${currentDesc}",
                        "isMultiline": true,
                        "maxLength": 16384
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Save",
                        "data": {
                            "id": "EditCard"
                        }
                    }
                ]
            }
            """;

            DataJson = new JsonObject
            {
                ["title"] = card.Name,
                ["currentTitle"] = card.Name,
                ["currentDesc"] = card.Desc,
            }.ToJsonString();
        }

        public override CommandResult SubmitForm(string payload)
        {
            // Verified from SampleContentForm.SubmitForm: JsonNode.Parse(payload)?.AsObject()
            var formInput = JsonNode.Parse(payload)?.AsObject();
            var newTitle = formInput?["CardTitle"]?.ToString() ?? string.Empty;
            var newDesc = formInput?["CardDescription"]?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newTitle))
                return CommandResult.KeepOpen();

            // Description is intentionally allowed to be empty - this clears the
            // description. The empty string is a valid, intentional value.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _trello.UpdateCardAsync(_card.Id, newTitle, newDesc, CancellationToken.None).ConfigureAwait(false);
                    _onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    var toast = new ToastStatusMessage($"Failed to edit card: {ex.Message}");
                    toast.Show();
                }
            });

            return CommandResult.GoBack();
        }
    }
}
