using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HomeNode.Services.Extraction;

// A deterministic, local extraction client: the mock/fixture path
// behind the model-role seam. It implements the extraction rules as
// plain pattern code over the context slice, with no model and no
// network, so the pipeline can be built, demonstrated, and tested
// end to end without spending API credit. It covers the utterance
// shapes in the worked examples the demo suite runs, including the
// Commitment, Task, and decision-note shapes; it is not a general
// language understander, and where a shape is outside its patterns
// it falls back to a plain HouseholdNote, or asks, and never guesses
// a person or a fact.
//
// Lookup answers are assembled only from RecentEvents facts: the
// mock restates the stored Body with the author's name in front.
// Producing the worked examples' polished restatements word for
// word is the live model's job; the fact content and the fence are
// identical.
public class MockExtractionClient : IExtractionClient
{
    public string Name => "mock";

    private static readonly string[] BodyStateWords =
    {
        "swollen", "swelling", "sprained", "hurts", "hurt", "pain", "sore",
        "aching", "ache", "dizzy", "fever", "nauseous", "bleeding", "bruised"
    };

    public Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
    {
        return Task.FromResult(Extract(input));
    }

    private static ExtractionOutput Extract(ExtractionInput input)
    {
        var output = new ExtractionOutput { UtteranceId = input.UtteranceId };
        var transcript = (input.Transcript ?? "").Trim();
        var ctx = input.Context;
        var speaker = input.Speaker;

        // Empty or pure noise is a valid, silent output.
        if (transcript.Length == 0 || !transcript.Any(char.IsLetter))
        {
            return output;
        }
        var lower = transcript.ToLowerInvariant();

        // Stated-audience marks (spec C.2.1 clause 3): where the
        // teller's words state an audience, the marks ride on the
        // events as proposals quoting the clause they came from.
        // Deterministic code downstream fences and decides; a mark
        // is evidence, never a judgment.
        var statedMarks = DetectStatedMarks(transcript, ctx);

        // Journal answer to an open prompt.
        if (ctx.OpenPrompt is { Kind: "Journal" } && speaker is not null)
        {
            output.Events.Add(new ExtractionEvent
            {
                Type = "Journal",
                Body = transcript,
                SubjectPersonId = speaker.PersonId,
                AuthorPersonId = speaker.PersonId,
                ConfirmationState = "Confirmed",
                Details = Details(("EntryKind", "PromptAnswer"), ("PromptId", ctx.OpenPrompt.PromptId)),
                StatedMarks = statedMarks
            });
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // The missed-dose-shaped self-report (worked example 12): a
        // plain verbatim note, deliberately before the health swallow,
        // because this shape is NOT health content for routing
        // purposes: no ScheduleRef, no action, no alert.
        if (Regex.IsMatch(lower, @"\bmissed (my|the)\b") && MentionsSchedule(lower, ctx))
        {
            output.Events.Add(Note(transcript, speaker, ("MedMention", "SelfReport")));
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // A statement about a schedule a human set (worked example 8):
        // ScheduleRef is copied from the slice, never invented.
        var schedule = MatchSchedule(lower, ctx);
        if (schedule is not null && lower.Contains(" set "))
        {
            output.Events.Add(Note(transcript, speaker,
                ("MedMention", "ScheduleStatement"),
                ("ScheduleRef", new JsonObject
                {
                    ["ScheduleId"] = schedule.ScheduleId,
                    ["TimeLocal"] = schedule.TimeLocal,
                    ["SetByPersonId"] = schedule.SetByPersonId
                })));
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // A casual medication mention (worked example 9): the medication
        // appears incidentally, as a location fact ("are in the blue
        // box"). A quantity statement ("only a few left in the bottle")
        // is a supply telling, not a casual mention, and does not match
        // the "are/is in the" shape.
        if ((lower.Contains("pills") || lower.Contains("medicine"))
            && (lower.Contains(" are in the ") || lower.Contains(" is in the "))
            && !HasHealthContent(lower))
        {
            output.Events.Add(Note(transcript, speaker, ("MedMention", "Casual")));
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // Priority rule 1: any health content swallows the utterance.
        if (HasHealthContent(lower))
        {
            return HealthMention(input, transcript, lower);
        }

        // An unclear relay: a pronoun with no resolvable referent and a
        // content-free "thing" (worked example 18). Ask, never guess.
        if (Regex.IsMatch(lower, @"^tell (her|him)\b") && lower.Contains("thing"))
        {
            var about = Regex.Match(transcript, @"about (\w+)", RegexOptions.IgnoreCase);
            var question = about.Success
                ? $"Who should I tell, and what about {about.Groups[1].Value}?"
                : "Who should I tell, and what should I tell them?";
            output.ClarificationNeeded = true;
            output.SpokenResponse = SpokenTemplates.Clarify(question);
            return output;
        }

        // Lookups: read only, write nothing.
        var lookup = TryLookup(input, transcript, lower);
        if (lookup is not null)
        {
            output.SpokenResponse = lookup;
            return output;
        }

        // A free journal entry, marked by the speaker.
        if (lower.Contains("for my journal") && speaker is not null)
        {
            output.Events.Add(new ExtractionEvent
            {
                Type = "Journal",
                Body = transcript,
                SubjectPersonId = speaker.PersonId,
                AuthorPersonId = speaker.PersonId,
                ConfirmationState = "Confirmed",
                Details = Details(("EntryKind", "FreeEntry")),
                StatedMarks = statedMarks
            });
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // A facility notice relayed by voice. A notice
        // carrying a medication-taking instruction was already swallowed
        // above as health content, so ChangeText here never carries one.
        var facility = Regex.Match(transcript, @"^(the|a)\s+(\w+)\s+called\b", RegexOptions.IgnoreCase);
        if (facility.Success)
        {
            var comma = transcript.IndexOf(',');
            var changeText = comma >= 0 ? transcript[(comma + 1)..].Trim().TrimEnd('.') : transcript;
            output.Events.Add(new ExtractionEvent
            {
                Type = "FacilityNotice",
                Body = transcript,
                SubjectPersonId = null,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = speaker is null ? "Proposed" : "Confirmed",
                Details = Details(("SourceName", $"the {facility.Groups[2].Value.ToLowerInvariant()}"), ("ChangeText", changeText))
            });
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // Everything else: split into clauses and classify each.
        var clauses = SplitClauses(transcript);
        var events = new List<ExtractionEvent>();
        ExtractionAction? action = null;
        foreach (var clause in clauses)
        {
            var (ev, act) = ClassifyClause(input, clause);
            events.Add(ev);
            if (act is not null && action is null)
            {
                action = act;
                act.EventIndex = events.Count - 1;
            }
        }

        // The event cap: at most five events; overflow folds
        // into the fifth event's Body verbatim, and any folded remainder
        // naming a person, a time, or a supply also lands as one
        // HouseholdNote, so nothing structured is silently lost.
        if (events.Count > 5)
        {
            var remainder = events.Skip(5).ToList();
            var foldedText = string.Join(", and ", remainder.Select(e => e.Body.TrimEnd('.')));
            events[4].Body = events[4].Body.TrimEnd('.') + ", and " + foldedText + ".";
            events = events.Take(5).ToList();
            if (action is not null && action.EventIndex > 4)
            {
                action = null;
            }
            if (remainder.Any(e => NamesPersonTimeOrSupply(e, ctx)))
            {
                events.Add(new ExtractionEvent
                {
                    Type = "HouseholdNote",
                    Body = foldedText + ".",
                    SubjectPersonId = speaker?.PersonId,
                    AuthorPersonId = speaker?.PersonId,
                    ConfirmationState = speaker is null ? "Proposed" : "Confirmed",
                    Details = Details(("MedMention", "None"))
                });
            }
        }

        foreach (var ev in events)
        {
            ev.StatedMarks = statedMarks;
        }
        output.Events = events;
        if (events.Any(e => e.Type == "Supply"))
        {
            // Supplies go to the brief, never to an immediate alert,
            // including inside a longer telling.
            output.AlertDecision = new ExtractionAlertDecision
            {
                Fire = false,
                FiresOn = "Never",
                LevelId = "brief",
                Reason = "SupplyGoesToBrief"
            };
        }
        if (action is not null)
        {
            output.ProposedAction = action;
            output.ConfirmationPrompt = BuildPrompt(ctx, action);
        }
        else if (events.Count == 1 && events[0].Type == "Supply" && events[0].RelatedSupplyItemId is int itemId)
        {
            var item = ctx.SupplyItems.First(s => s.SupplyItemId == itemId);
            var direction = events[0].Details.TryGetPropertyValue("Direction", out var d) ? d?.GetValue<string>() ?? "Low" : "Low";
            output.SpokenResponse = SpokenTemplates.Supply(item.Name, SpokenTemplates.DirectionWord(direction));
            output.AlertDecision = new ExtractionAlertDecision
            {
                Fire = false,
                FiresOn = "Never",
                LevelId = "brief",
                Reason = "SupplyGoesToBrief"
            };
        }
        else if (events.Any(e => e.Type == "Supply"))
        {
            output.AlertDecision = new ExtractionAlertDecision
            {
                Fire = false,
                FiresOn = "Never",
                LevelId = "brief",
                Reason = "SupplyGoesToBrief"
            };
            output.SpokenResponse = events.Count == 1 ? SpokenTemplates.Recorded : null;
        }
        else
        {
            output.SpokenResponse = SpokenTemplates.Recorded;
        }
        if (action is not null && events[action.EventIndex].Type == "Request"
            && events[action.EventIndex].Details.TryGetPropertyValue("RequestKind", out var kind)
            && kind?.GetValue<string>() == "Relay")
        {
            output.AlertDecision = new ExtractionAlertDecision
            {
                Fire = false,
                FiresOn = "Never",
                Reason = "RelayIsAnActionNotAnAlert"
            };
        }
        return output;
    }

    // Priority rule 1's landing: one HealthMention, the whole utterance
    // verbatim, and the only speech the templates allow for it.
    private static ExtractionOutput HealthMention(ExtractionInput input, string transcript, string lower)
    {
        var ctx = input.Context;
        var speaker = input.Speaker;
        var isQuestion = transcript.EndsWith('?')
            || Regex.IsMatch(lower, @"^(should|is|can|do|does|what|why)\b");
        var output = new ExtractionOutput { UtteranceId = input.UtteranceId };

        // The subject is another person only when the speaker names one
        // alongside the body content; otherwise it is the speaker.
        int? subjectId = speaker?.PersonId;
        foreach (var person in ctx.Persons)
        {
            if (speaker is not null && person.PersonId != speaker.PersonId
                && lower.Contains(person.FirstName.ToLowerInvariant()))
            {
                subjectId = person.PersonId;
                break;
            }
        }

        var ev = new ExtractionEvent
        {
            Type = "HealthMention",
            Body = transcript,
            SubjectPersonId = subjectId,
            AuthorPersonId = speaker?.PersonId,
            Details = Details(("Form", isQuestion ? "Question" : "Statement"))
        };
        output.Events.Add(ev);

        // A family-authored mention about another person is record-only:
        // the author already knows.
        if (speaker is null || subjectId != speaker.PersonId)
        {
            ev.ConfirmationState = speaker is null ? "Proposed" : "Confirmed";
            output.SpokenResponse = SpokenTemplates.Recorded;
            return output;
        }

        // Route to the default route person; if the author IS the route
        // person for their own mention, the backup stands in.
        var routeId = ctx.DefaultRoutePersonId;
        var backupId = ctx.BackupRoutePersonId;
        if (routeId == speaker.PersonId)
        {
            (routeId, backupId) = (backupId, routeId);
        }
        var route = ctx.Persons.FirstOrDefault(p => p.PersonId == routeId);
        ev.ConfirmationState = "Proposed";
        output.ProposedAction = new ExtractionAction
        {
            ActionType = "NotifyPerson",
            TargetPersonId = routeId,
            EventIndex = 0,
            Payload = new JsonObject { ["AboutEventIndex"] = 0 }
        };
        // For a health mention the spoken response IS the confirmation
        // question, in the template's full form (worked example 7: the
        // same sentence in both fields).
        var routeSentence = route is null
            ? SpokenTemplates.Recorded
            : isQuestion
                ? SpokenTemplates.RouteQuestion(route.FirstName)
                : SpokenTemplates.Route(route.FirstName);
        output.ConfirmationPrompt = routeSentence;
        output.SpokenResponse = routeSentence;
        output.AlertDecision = new ExtractionAlertDecision
        {
            Fire = false,
            FiresOn = "Confirmation",
            LevelId = ctx.HealthMentionLevelId ?? "now",
            RouteToPersonId = routeId,
            BackupPersonId = backupId,
            Reason = "HealthMentionRouteOnConfirmation"
        };
        return output;
    }

    private static string? TryLookup(ExtractionInput input, string transcript, string lower)
    {
        var ctx = input.Context;
        // "Where's Daniel?" answered from the stored whereabouts.
        var where = Regex.Match(lower, @"^where(?:'s| is)\s+(\w+)");
        if (where.Success)
        {
            var person = FindPerson(ctx, where.Groups[1].Value);
            if (person is null)
            {
                return SpokenTemplates.DoNotKnow;
            }
            var ev = ctx.RecentEvents.LastOrDefault(e =>
                e.Type == "Whereabouts" && e.SubjectPersonId == person.PersonId && !IsFenced(e));
            if (ev is null)
            {
                return SpokenTemplates.DoNotKnow;
            }
            return ev.Body.StartsWith("I'm at", StringComparison.OrdinalIgnoreCase)
                ? $"{person.FirstName} said he's{ev.Body[4..]}"
                : $"{person.FirstName} said: {ev.Body}";
        }
        // "What did Daniel say about Tuesday?"
        var what = Regex.Match(lower, @"^what did\s+(\w+)\s+say about\s+(\w+)");
        if (what.Success)
        {
            var person = FindPerson(ctx, what.Groups[1].Value);
            var topic = what.Groups[2].Value;
            if (person is null)
            {
                return SpokenTemplates.DoNotKnow;
            }
            var ev = ctx.RecentEvents.LastOrDefault(e =>
                e.AuthorPersonId == person.PersonId
                && e.Body.Contains(topic, StringComparison.OrdinalIgnoreCase)
                && !IsFenced(e));
            return ev is null
                ? SpokenTemplates.DoNotKnow
                : $"{person.FirstName} said: {ev.Body}";
        }
        return null;
    }

    // The lookup fence: health mentions and any event
    // whose words carry a medication-taking instruction are never a
    // lookup source.
    private static bool IsFenced(ExtractionEvent ev)
    {
        if (ev.Type == "HealthMention")
        {
            return true;
        }
        var body = ev.Body.ToLowerInvariant();
        return Regex.IsMatch(body, @"\b(take|skip|stop taking)\b[^.]*\b(pill|pills|ones|medicine|extra)\b");
    }

    private static (ExtractionEvent, ExtractionAction?) ClassifyClause(ExtractionInput input, string clause)
    {
        var ctx = input.Context;
        var speaker = input.Speaker;
        var lower = clause.ToLowerInvariant();
        var state = speaker is null ? "Proposed" : "Confirmed";

        // Supply: resolve the item by name or alias, longest match wins.
        var item = ResolveSupply(ctx, lower);
        var direction = SupplyDirection(lower);
        if (item is not null && direction is not null)
        {
            // A relay-framed supply statement ("Tell Daniel the gauze is
            // nearly gone") is about the speaker's own supply; a plain
            // statement of fact is about the item, subject null.
            var relayFramed = Regex.IsMatch(lower, @"^tell\s+\w+\s");
            return (new ExtractionEvent
            {
                Type = "Supply",
                Body = clause,
                SubjectPersonId = relayFramed ? speaker?.PersonId : null,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                RelatedSupplyItemId = item.SupplyItemId,
                Details = Details(("Direction", direction), ("QuantityText", QuantityText(clause, lower)))
            }, null);
        }
        if (item is null && direction is not null)
        {
            var unresolved = Regex.Match(clause, @"^(.+?)\s+is\s+(low|out)\b", RegexOptions.IgnoreCase);
            if (unresolved.Success)
            {
                return (new ExtractionEvent
                {
                    Type = "Supply",
                    Body = clause,
                    SubjectPersonId = null,
                    AuthorPersonId = speaker?.PersonId,
                    ConfirmationState = "Proposed",
                    Details = Details(("Direction", "Low"), ("UnresolvedItem", unresolved.Groups[1].Value.Trim().ToLowerInvariant()))
                }, null);
            }
        }

        // Whereabouts, first person ("I'm at Domino's, heading home
        // after the store") or third person ("Daniel is at the store").
        var whereFirst = Regex.Match(clause, @"^I(?:'m| am) at\s+([^,]+)(?:,\s*(.+))?$", RegexOptions.IgnoreCase);
        if (whereFirst.Success && speaker is not null)
        {
            var rest = whereFirst.Groups[2].Value;
            var details = new JsonObject
            {
                ["PlaceText"] = whereFirst.Groups[1].Value.Trim(),
                ["Motion"] = rest.Contains("heading home", StringComparison.OrdinalIgnoreCase) ? "HeadingHome"
                    : rest.Contains("heading", StringComparison.OrdinalIgnoreCase) ? "HeadingTo" : "At"
            };
            var after = Regex.Match(rest, @"after\s+(.+)$", RegexOptions.IgnoreCase);
            if (after.Success)
            {
                details["NextText"] = after.Groups[1].Value.Trim().TrimEnd('.');
            }
            return (new ExtractionEvent
            {
                Type = "Whereabouts",
                Body = clause,
                SubjectPersonId = speaker.PersonId,
                AuthorPersonId = speaker.PersonId,
                ConfirmationState = "Confirmed",
                Details = details
            }, null);
        }
        var whereThird = Regex.Match(clause, @"^(\w+) is at\s+(.+)$", RegexOptions.IgnoreCase);
        if (whereThird.Success && FindPerson(ctx, whereThird.Groups[1].Value) is { } located)
        {
            return (new ExtractionEvent
            {
                Type = "Whereabouts",
                Body = clause,
                SubjectPersonId = located.PersonId,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                Details = Details(("PlaceText", whereThird.Groups[2].Value.Trim().TrimEnd('.')), ("Motion", "At"))
            }, null);
        }

        // Requests.
        var relay = Regex.Match(clause, @"^(?:could you\s+)?ask\s+(\w+)\s+to\s+", RegexOptions.IgnoreCase);
        if (relay.Success && FindPerson(ctx, relay.Groups[1].Value) is { } relayTarget && speaker is not null)
        {
            var ev = new ExtractionEvent
            {
                Type = "Request",
                Body = clause,
                SubjectPersonId = speaker.PersonId,
                AuthorPersonId = speaker.PersonId,
                ConfirmationState = "Proposed",
                Details = Details(("RequestKind", "Relay"), ("TargetPersonId", relayTarget.PersonId))
            };
            var act = new ExtractionAction
            {
                ActionType = "RelayMessage",
                TargetPersonId = relayTarget.PersonId,
                Payload = new JsonObject { ["MessageText"] = clause }
            };
            return (ev, act);
        }
        if (lower.StartsWith("remind me"))
        {
            var remind = Regex.Match(clause, @"^remind me on\s+(.+?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
            if (remind.Success && speaker is not null)
            {
                var ev = new ExtractionEvent
                {
                    Type = "Request",
                    Body = clause,
                    SubjectPersonId = speaker.PersonId,
                    AuthorPersonId = speaker.PersonId,
                    ConfirmationState = "Proposed",
                    Details = Details(("RequestKind", "Reminder"))
                };
                var act = new ExtractionAction
                {
                    ActionType = "CreateReminder",
                    Payload = new JsonObject
                    {
                        ["RemindOnText"] = remind.Groups[1].Value.Trim(),
                        ["TaskText"] = remind.Groups[2].Value.Trim().TrimEnd('.')
                    }
                };
                return (ev, act);
            }
        }
        var mediaTerm = ctx.Glossary.FirstOrDefault(g => lower.Contains(g.Term.ToLowerInvariant()));
        if (mediaTerm is not null && (lower.Contains("put ") || lower.Contains("comes on") || lower.Contains(" on")))
        {
            var at = Regex.Match(clause, @"\bat\s+(\w+)\.?$", RegexOptions.IgnoreCase);
            var ev = new ExtractionEvent
            {
                Type = "Request",
                Body = clause,
                SubjectPersonId = speaker?.PersonId,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = "Proposed",
                Details = Details(("RequestKind", "Media"))
            };
            var act = new ExtractionAction
            {
                ActionType = "MediaPlay",
                Payload = new JsonObject
                {
                    ["WhatText"] = mediaTerm.Term,
                    ["AtLocal"] = lower.Contains("comes on at") && at.Success ? at.Groups[1].Value : null
                }
            };
            return (ev, act);
        }

        // A commitment: the speaker binds their own future action.
        if (speaker is not null && Regex.IsMatch(lower, @"^(i'll|i will)\b"))
        {
            var details = new JsonObject { ["CommitmentText"] = clause.TrimEnd('.') };
            var (dueText, dueAt) = ParseDue(clause, input.OccurredAtLocal);
            if (dueText is not null)
            {
                details["DueText"] = dueText;
            }
            if (dueAt is not null)
            {
                details["DueAtLocal"] = dueAt.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            }
            return (new ExtractionEvent
            {
                Type = "Commitment",
                Body = clause,
                SubjectPersonId = speaker.PersonId,
                AuthorPersonId = speaker.PersonId,
                ConfirmationState = "Confirmed",
                Details = details
            }, null);
        }

        // A task: work that needs doing, not yet anyone's promise.
        var pickup = Regex.Match(clause, @"^(\w+) needs picking up(?:\s+from\s+(.+?))?(?:\s+at\s+(.+))?$", RegexOptions.IgnoreCase);
        if (pickup.Success && FindPerson(ctx, pickup.Groups[1].Value) is { } collect)
        {
            var details = new JsonObject
            {
                ["TaskKind"] = "Pickup",
                ["TaskText"] = "picking up",
                ["CollectPersonId"] = collect.PersonId,
                ["NeedsAssignee"] = true
            };
            if (pickup.Groups[2].Success)
            {
                details["PlaceText"] = pickup.Groups[2].Value.Trim().TrimEnd('.');
            }
            if (pickup.Groups[3].Success)
            {
                details["DueText"] = pickup.Groups[3].Value.Trim().TrimEnd('.');
            }
            var (_, pickupDueAt) = ParseDue(clause, input.OccurredAtLocal);
            if (pickupDueAt is not null)
            {
                details["DueAtLocal"] = pickupDueAt.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            }
            return (new ExtractionEvent
            {
                Type = "Task",
                Body = clause,
                SubjectPersonId = collect.PersonId,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                Details = details
            }, null);
        }
        // An open household decision spoken as a question ("And what
        // are we even doing for dinner?"): a HouseholdNote carrying
        // the DecisionTopic and DecisionState keys, so the undecided
        // thing has a structured existence and the
        // Plan assembler can pick it up. Nobody's subject; the
        // speaker is its author.
        var dinner = Regex.Match(clause, @"^(?:and\s+)?what are we (?:even )?(?:doing|having|eating|making) for (\w+)", RegexOptions.IgnoreCase);
        if (dinner.Success)
        {
            return (new ExtractionEvent
            {
                Type = "HouseholdNote",
                Body = clause,
                SubjectPersonId = null,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                Details = Details(
                    ("DecisionTopic", dinner.Groups[1].Value.ToLowerInvariant()),
                    ("DecisionState", "Undecided"))
            }, null);
        }

        // An errand spoken as an imperative ("Grab groceries on the
        // way back"): a Task, TaskKind Errand, nobody's subject.
        var errand = Regex.Match(clause, @"^(grab|get|pick up|collect|buy)\s+(.+)$", RegexOptions.IgnoreCase);
        if (errand.Success)
        {
            var rest = errand.Groups[2].Value.Trim().TrimEnd('.');
            string? whenText = null;
            var onTheWay = Regex.Match(rest, @"^(.*?)\s+(on the way(?: back| home)?)$", RegexOptions.IgnoreCase);
            if (onTheWay.Success)
            {
                rest = onTheWay.Groups[1].Value.Trim();
                whenText = onTheWay.Groups[2].Value.Trim();
            }
            var details = new JsonObject
            {
                ["TaskKind"] = "Errand",
                ["TaskText"] = rest,
                ["NeedsAssignee"] = true
            };
            if (whenText is not null)
            {
                details["WhenText"] = whenText;
            }
            return (new ExtractionEvent
            {
                Type = "Task",
                Body = clause,
                SubjectPersonId = null,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                Details = details
            }, null);
        }

        if (lower.Contains("homework"))
        {
            // The named-child form ("Mia has math homework tonight
            // and she is stuck"): the child is the subject, the
            // subject word rides in TaskText, and HelpFlag is set when
            // the telling says she is stuck.
            var named = Regex.Match(clause, @"^(\w+) has (?:(\w+) )?homework\b", RegexOptions.IgnoreCase);
            var subject = named.Success ? FindPerson(ctx, named.Groups[1].Value) : null;
            var details = new JsonObject
            {
                ["TaskKind"] = "General",
                ["TaskText"] = named.Success && named.Groups[2].Success
                    ? $"{named.Groups[2].Value.ToLowerInvariant()} homework"
                    : "homework",
                ["HelpFlag"] = lower.Contains("stuck") || lower.Contains("help")
            };
            var when = Regex.Match(lower, @"\b(tonight|today|tomorrow|this evening)\b");
            if (when.Success)
            {
                details["WhenText"] = when.Groups[1].Value;
            }
            return (new ExtractionEvent
            {
                Type = "Task",
                Body = clause,
                SubjectPersonId = subject?.PersonId ?? speaker?.PersonId,
                AuthorPersonId = speaker?.PersonId,
                ConfirmationState = state,
                Details = details
            }, null);
        }

        // The fallback: a plain note, recorded as said.
        return (Note(clause, speaker, ("MedMention", "None")), null);
    }

    // The mock's stated-audience patterns (spec C.2.1 clause 3).
    // Only a stated phrase produces a mark; the topic or tone of a
    // telling never does. Each mark quotes its clause from the
    // transcript exactly as spoken, and a named mark is produced
    // only when the name resolves to a person in the slice; the
    // deterministic fence downstream re-checks both facts anyway.
    private static List<ProposedStatedAudience> DetectStatedMarks(string transcript, ExtractionContext ctx)
    {
        var marks = new List<ProposedStatedAudience>();
        void Named(string pattern)
        {
            var match = Regex.Match(transcript, pattern, RegexOptions.IgnoreCase);
            if (match.Success && FindPerson(ctx, match.Groups[1].Value) is { } person)
            {
                marks.Add(new ProposedStatedAudience
                {
                    Kind = "Named",
                    PersonIds = new List<int> { person.PersonId },
                    Clause = match.Value
                });
            }
        }
        void Simple(string pattern, string kind)
        {
            var match = Regex.Match(transcript, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                marks.Add(new ProposedStatedAudience { Kind = kind, Clause = match.Value });
            }
        }
        Named(@"keep this between (\w+) and me");
        Named(@"\bjust for (\w+)");
        Named(@"\bonly for (\w+)");
        Named(@"\bfor (\w+) only\b");
        Named(@"\btell only (\w+)");
        Named(@"\bonly (\w+) should (?:see|hear) this");
        Simple(@"\bkeep this private\b", "Personal");
        Simple(@"\bjust between us\b", "Personal");
        Simple(@"\bthis stays private\b", "Personal");
        Simple(@"\bshare (?:this|it) with the household\b", "Household");
        Simple(@"\bfor the whole household\b", "Household");
        Simple(@"\btell the whole household\b", "Household");
        Simple(@"\bthe whole household can see this\b", "Household");
        Simple(@"\bshare this\b(?!\s+with)", "Unclear");
        Simple(@"\bpass this on\b", "Unclear");
        return marks;
    }

    private static ExtractionEvent Note(string body, ExtractionPersonRef? speaker, params (string Key, object? Value)[] details) =>
        new()
        {
            Type = "HouseholdNote",
            Body = body,
            SubjectPersonId = speaker?.PersonId,
            AuthorPersonId = speaker?.PersonId,
            ConfirmationState = speaker is null ? "Proposed" : "Confirmed",
            Details = Details(details)
        };

    private static JsonObject Details(params (string Key, object? Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            obj[key] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                JsonNode node => node.DeepClone(),
                _ => JsonValue.Create(value.ToString())
            };
        }
        return obj;
    }

    private static bool HasHealthContent(string lower)
    {
        if (BodyStateWords.Any(w => Regex.IsMatch(lower, $@"\b{w}\b")))
        {
            return true;
        }
        if (Regex.IsMatch(lower, @"^(should|can|could) i\b"))
        {
            return true;
        }
        if (lower.Contains("take an extra") || lower.Contains("extra one"))
        {
            return true;
        }
        if (Regex.IsMatch(lower, @"\btake (two|one|your|the)\b[^.]*\b(pill|pills|ones)\b"))
        {
            return true;
        }
        return false;
    }

    private static bool MentionsSchedule(string lower, ExtractionContext ctx) =>
        ctx.Schedules.Any(s => lower.Contains(s.Label.ToLowerInvariant()))
        || lower.Contains(" ones");

    private static ExtractionSliceSchedule? MatchSchedule(string lower, ExtractionContext ctx) =>
        ctx.Schedules.FirstOrDefault(s => lower.Contains(s.Label.ToLowerInvariant()));

    private static ExtractionSliceSupply? ResolveSupply(ExtractionContext ctx, string lower)
    {
        ExtractionSliceSupply? best = null;
        var bestLength = 0;
        foreach (var item in ctx.SupplyItems)
        {
            foreach (var candidate in item.Aliases.Prepend(item.Name))
            {
                var needle = candidate.ToLowerInvariant();
                if (needle.Length > bestLength && lower.Contains(needle))
                {
                    best = item;
                    bestLength = needle.Length;
                }
            }
        }
        return best;
    }

    private static string? SupplyDirection(string lower)
    {
        if (lower.Contains("nearly gone") || lower.Contains("running low") || lower.Contains(" low")
            || lower.Contains("only a few") || lower.StartsWith("low"))
        {
            return "Low";
        }
        if (lower.Contains("out of") || lower.Contains("all gone"))
        {
            return "Out";
        }
        if (lower.Contains("last of") || lower.Contains("used"))
        {
            return "Used";
        }
        if (lower.Contains("brought") || lower.Contains("restocked") || lower.Contains("got more"))
        {
            return "Restocked";
        }
        return null;
    }

    private static string? QuantityText(string clause, string lower)
    {
        if (lower.Contains("nearly gone"))
        {
            return "nearly gone";
        }
        // "Only a few of the evening pills left" states the quantity of
        // the item itself; a trailing "there are only a few left" about
        // an already-named item does not (worked examples 17 and 5).
        if (lower.Contains("only a few of the"))
        {
            return "only a few left";
        }
        var lastOf = Regex.Match(clause, @"last of the\s+\w+", RegexOptions.IgnoreCase);
        return lastOf.Success ? lastOf.Value : null;
    }

    private static ExtractionSlicePerson? FindPerson(ExtractionContext ctx, string name) =>
        ctx.Persons.FirstOrDefault(p =>
            p.FirstName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static List<string> SplitClauses(string transcript)
    {
        // A telling may run to several sentences (the afternoon ramble
        // is four). Split on sentence boundaries first, keeping each
        // sentence's own terminal punctuation, then on ", and " inside
        // a sentence, the way the multi-clause worked examples join.
        var sentences = Regex.Split(transcript, @"(?<=[.?!])\s+(?=[A-Z])")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
        var clauses = new List<string>();
        foreach (var sentence in sentences)
        {
            if (sentence.Contains(", and ", StringComparison.OrdinalIgnoreCase))
            {
                clauses.AddRange(sentence.Split(new[] { ", and " }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .Where(c => c.Length > 0));
            }
            else
            {
                clauses.Add(sentence);
            }
        }
        return clauses;
    }

    private static bool NamesPersonTimeOrSupply(ExtractionEvent ev, ExtractionContext ctx)
    {
        if (ev.SubjectPersonId is not null || ev.RelatedSupplyItemId is not null)
        {
            return true;
        }
        var lower = ev.Body.ToLowerInvariant();
        return ctx.Persons.Any(p => lower.Contains(p.FirstName.ToLowerInvariant()))
            || Regex.IsMatch(lower, @"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b")
            || ev.Details.ContainsKey("UnresolvedItem");
    }

    private static string BuildPrompt(ExtractionContext ctx, ExtractionAction action)
    {
        var target = ctx.Persons.FirstOrDefault(p => p.PersonId == action.TargetPersonId);
        return action.ActionType switch
        {
            "RelayMessage" => SpokenTemplates.RelayConfirm(
                target?.FirstName ?? "",
                action.Payload.TryGetPropertyValue("MessageText", out var m) ? m?.GetValue<string>() ?? "" : ""),
            "MediaPlay" => SpokenTemplates.MediaConfirm(
                action.Payload.TryGetPropertyValue("WhatText", out var w) ? w?.GetValue<string>() ?? "" : ""),
            "CreateReminder" => SpokenTemplates.RemindConfirm(
                action.Payload.TryGetPropertyValue("RemindOnText", out var r) ? r?.GetValue<string>() ?? "" : "",
                action.Payload.TryGetPropertyValue("TaskText", out var t) ? t?.GetValue<string>() ?? "" : ""),
            "NotifyPerson" => target is null ? "" : $"Do you want me to tell {target.FirstName}?",
            _ => ""
        };
    }

    private static readonly Dictionary<string, int> HourWords = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12
    };

    // The mock's small clock rule for "by three" and "at five thirty":
    // household times after noon read as PM; a time already past rolls
    // to the next day. The live model resolves the same words against
    // the same clock; this exists so fixtures have one stated rule.
    private static (string? DueText, DateTimeOffset? DueAt) ParseDue(string clause, DateTimeOffset occurred)
    {
        var m = Regex.Match(clause,
            @"\b(?:by|at)\s+(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b(?:\s+(thirty|fifteen|forty-five))?",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return (null, null);
        }
        var hour = HourWords[m.Groups[1].Value.ToLowerInvariant()];
        if (hour < 12)
        {
            hour += 12;
        }
        var minute = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "thirty" => 30,
            "fifteen" => 15,
            "forty-five" => 45,
            _ => 0
        };
        var due = new DateTimeOffset(occurred.Year, occurred.Month, occurred.Day, hour, minute, 0, occurred.Offset);
        if (due <= occurred)
        {
            due = due.AddDays(1);
        }
        return (m.Groups[1].Value.ToLowerInvariant() + (m.Groups[2].Success ? " " + m.Groups[2].Value.ToLowerInvariant() : ""), due);
    }
}
