using System.Collections;
using System.Globalization;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

internal static class InvestmentTableUiChecks
{
    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Real frames draw the reward table pages and save table fields from them and from an item page. */
    public static void Run(string install, string temporary, string? keys)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckFrames(install, temporary, keys); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Investment table UI check failed.", failure);
    }

    private static void CheckFrames(string install, string temporary, string? keys)
    {
        var project = StudioProject.Create(Path.Combine(temporary, "ui-tables"), "Reward tables");
        string settings = Path.Combine(temporary, "ui-tables-settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Warp: true, Smoke: true, Frames: 20000,
            WaitForNames: true, SettingsPath: settings, KeySource: keys));
        int phase = 0, undo = 0, pool = -1, wrapper = -1, quest = -1, steps = 0, itemRow = -1, replacement = -1, other = -1;
        var questPage = form.Pages.Pages.OfType<QuestPage>().Single();
        StudioDocument? shownDocument = null;
        uint originalItem = 0;
        uint passTag = 0, poolTag = 0, quantity = 0;
        string Shown() => (string?)typeof(StudioForm).GetField("shownPage", Access)!.GetValue(form) ?? "";
        int Undo() => ((ICollection)typeof(StudioWorkspace).GetField("undo", Access)!.GetValue(form.Workspace)!).Count;
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
            if (!LiveUi.Catalog(form, out var catalog)) return;
            var tables = catalog.Tables();
            var rewards = form.Pages.Pages.OfType<ItemRewardsPage>().Single();
            switch (phase)
            {
                case 0:
                    passTag = tables.TableTag(InvestmentRootSlots.Progressions); poolTag = tables.TableTag(InvestmentRootSlots.Pools);
                    Check(passTag != 0 && poolTag != 0, "The investment root names no progression or pool table.");
                    Check(work.Catalog!.Entries.Count(entry => entry.Reference == InvestmentSeasonPass.TableClass) == 1
                        && work.Catalog.Entries.Count(entry => entry.Reference == InvestmentRewards.TableClass) == 1,
                        "The reward table classes are not unique; the browser category would list other tables.");
                    work.Open(passTag); form.ShowAssetPage(SeasonPassPage.PageId); phase++; break;
                case 1:
                {
                    if (Shown() != "asset:" + SeasonPassPage.PageId) return;
                    var row = tables.PassCandidates.MaxBy(ladder => ladder.Rewards.Count)!.Rewards[0];
                    quantity = row.Quantity; undo = Undo();
                    Check(Save(form.Pages.Pages.OfType<SeasonPassPage>().Single(), work, row.QuantityField, quantity + 1), "Season pass quantity did not save.");
                    phase++; break;
                }
                case 2:
                {
                    var ladder = tables.PassCandidates.MaxBy(ladder => ladder.Rewards.Count)!;
                    Check(work.Document?.Entry.TagHash == passTag && work.Project!.ContainsEdit(passTag) && Undo() == undo + 1
                        && ladder.Rewards[0].Quantity == quantity + 1, "The pass row edit did not save one undo step on the open table.");
                    var page = form.Pages.Pages.OfType<SeasonPassPage>().Single();
                    Check(Changed(page, work, ladder.Rewards[0].QuantityField) && !Changed(page, work, ladder.Rewards[0].ItemField),
                        "The pending mark does not follow the edited field.");
                    // A row without sockets given another plain item, which the popup lets Save.
                    var row = ladder.Rewards.First(row => row.Sockets.Count == 0 && row.Error.Length == 0 && row.Index != 0);
                    replacement = catalog.Items.First(item => item.Named && item.Index != row.ItemIndex
                        && SeasonPassPage.Replacement(catalog, tables, row, item.Index) is { Refusal: "" }).Index;
                    itemRow = row.Index; originalItem = row.ItemIndex; undo = Undo();
                    Check(Save(page, work, row.ItemField, replacement), "The pass row's item did not save through the popup.");
                    phase++; break;
                }
                case 3:
                {
                    var ladder = tables.PassCandidates.MaxBy(ladder => ladder.Rewards.Count)!;
                    var page = form.Pages.Pages.OfType<SeasonPassPage>().Single();
                    Check(ladder.Rewards[itemRow].ItemIndex == replacement && Undo() == undo + 1 && Changed(page, work, ladder.Rewards[itemRow].ItemField)
                        && !Changed(page, work, ladder.Rewards[itemRow].QuantityField), "The item replacement did not save one pending step on its field.");
                    work.Undo(); phase++; break;
                }
                case 4:
                {
                    var ladder = tables.PassCandidates.MaxBy(ladder => ladder.Rewards.Count)!;
                    Check(ladder.Rewards[itemRow].ItemIndex == originalItem && Undo() == undo
                        && !Changed(form.Pages.Pages.OfType<SeasonPassPage>().Single(), work, ladder.Rewards[itemRow].ItemField)
                        && ladder.Rewards[0].Quantity == quantity + 1, "Undo did not restore the pass row's item alone.");
                    pool = Enumerable.Range(0, tables.Pools.Count).First(index => tables.RewardGraph.WrappersOf(index).Count != 0
                        && tables.Pools[index].Entries.Count != 0);
                    wrapper = tables.RewardGraph.WrappersOf(pool)[0];
                    work.Open(poolTag); form.ShowAssetPage(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(pool)); phase++; break;
                }
                case 5:
                    if (Shown() != "asset:" + RewardPoolsPage.PageId) return;
                    Check((int)typeof(RewardPoolsPage).GetField("selected", Access)!.GetValue(form.Pages.Pages.OfType<RewardPoolsPage>().Single())! == pool,
                        "The reward pools page did not select the requested pool.");
                    work.Open(catalog.Items[wrapper].Tag); form.ShowAssetPage(ItemRewardsPage.PageId); phase++; break;
                case 6:
                {
                    if (Shown() != "asset:" + ItemRewardsPage.PageId || !work.InvestmentDetailCurrent || work.SelectedInvestmentItem?.Wrapper is null) return;
                    var entry = tables.Pools[pool].Entries[0];
                    undo = Undo();
                    Check(Save(rewards, work, entry.WeightField, entry.Weight * 2 + 1), "The item's Rewards page did not save a pool weight.");
                    phase++; break;
                }
                case 7:
                    Check(work.Document?.Entry.TagHash == catalog.Items[wrapper].Tag && work.Project!.ContainsEdit(poolTag) && Undo() == undo + 1,
                        "A pool edit from the item's Rewards page did not keep the item open with one undo step.");
                    Begin(rewards, work, tables.Pools[pool].Entries[0].QuantityField);
                    work.Undo(); phase++; break;
                case 8:
                    Check(Field(Popup(rewards)) is null, "A table draft survived an overlay change.");
                    var members = Enumerable.Range(0, catalog.Items.Count).Where(item => tables.QuestOwner(item) >= 0)
                        .GroupBy(tables.QuestOwner).First(chain => chain.Count() >= 2);
                    quest = members.Key;
                    work.Open(catalog.Items[members.ElementAt(1)].Tag); form.ShowAssetPage("sunrise.investment"); phase++; break;
                case 9:
                {
                    if (Shown() != "asset:sunrise.investment" || !work.InvestmentDetailCurrent) return;
                    var chain = work.SelectedInvestmentItem?.Pursuit?.Chain;
                    Check(chain?.OwnerItem == quest, $"The quest step did not read its chain owner {quest}: "
                        + string.Join("; ", work.SelectedInvestmentItem?.Diagnostics ?? []) + chain?.Error);
                    steps = chain!.Steps.Count;
                    // The Quest page on another step of the chain than the open one, as a click on its node chooses it.
                    other = chain.Steps.First(step => step.ItemIndex != work.SelectedInvestmentItem!.Item.Index).Member;
                    form.ShowAssetPage(QuestPage.PageId, QuestPage.StepRow(other)); phase++; break;
                }
                case 10:
                {
                    if (Shown() != "asset:" + QuestPage.PageId || questPage.Row != QuestPage.StepRow(other)) return;
                    // A target edit saves a delta and reads the open step into a new document.
                    var objective = tables.Objectives[work.SelectedInvestmentItem!.Pursuit!.Objectives[0]];
                    shownDocument = work.Document;
                    work.Run(() => work.ApplyInvestmentTableEdit(objective.CompletionField, $"{objective.CompletionValue + 1}"));
                    phase++; break;
                }
                case 11:
                case 12:
                    if (Shown() != "asset:" + QuestPage.PageId || ReferenceEquals(work.Document, shownDocument) || !work.InvestmentDetailCurrent) return;
                    Check(questPage.Row == QuestPage.StepRow(other),
                        $"The Quest page left step {other} for {questPage.Row} after the {(phase == 11 ? "edit" : "undo")} read a new document.");
                    shownDocument = work.Document;
                    if (phase == 11) work.Undo(); else form.Close();
                    phase++; break;
            }
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        int frames = (int)typeof(StudioForm).GetField("smokeFrames", Access)!.GetValue(form)!;
        Check(phase == 13, $"Investment table UI stopped at step {phase} after {frames} frames.");
        Console.WriteLine($"investment_tables_ui pass_edit=pass pass_item={itemRow}:{originalItem}>{replacement} pool={pool} item_edit=kept_document draft=expired quest_owner={quest} steps={steps} quest_step=kept frames={frames}");
    }

    private static object Popup(object page) => page.GetType().GetField("popup", Access)!.GetValue(page)!;
    private static object? Field(object popup) => popup.GetType().GetProperty("Field")!.GetValue(popup);

    /** Opens a table draft on the page's popup, as a table cell's button does. */
    private static void Begin(object page, StudioWorkspace workspace, InvestmentTableField field)
    {
        var popup = Popup(page);
        popup.GetType().GetMethod("Begin", [typeof(StudioWorkspace), typeof(InvestmentTableField), typeof(string)])!.Invoke(popup, [workspace, field, "check"]);
    }

    /** Opens a table draft, types the value and saves it as the popup's button would. */
    private static bool Save(object page, StudioWorkspace workspace, InvestmentTableField field, object value)
    {
        Begin(page, workspace, field);
        var popup = Popup(page);
        popup.GetType().GetProperty("Value")!.SetValue(popup, Convert.ToString(value, CultureInfo.InvariantCulture));
        return (bool)popup.GetType().GetMethod("Save")!.Invoke(popup, [workspace])!;
    }

    private static bool Changed(object page, StudioWorkspace workspace, InvestmentTableField field)
    {
        var marks = page.GetType().GetField("pending", Access)!.GetValue(page)!;
        return (bool)marks.GetType().GetMethod("Changed")!.Invoke(marks, [workspace, field])!;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
