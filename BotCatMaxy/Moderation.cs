﻿using MongoDB.Bson.Serialization.Attributes;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using BotCatMaxy.Settings;
using Discord.WebSocket;
using Discord.Commands;
using BotCatMaxy.Data;
using System.Linq;
using BotCatMaxy;
using Humanizer;
using System.IO;
using Discord;
using System;
using Discord.Addons.Interactive;

namespace BotCatMaxy {
    public class Infraction {
        [BsonDateTimeOptions(Kind = DateTimeKind.Local)]
        public DateTime time;
        public string logLink;
        public string reason;
        public float size;
    }

    public static class ModerationFunctions {
        public static async Task Warn(this SocketGuildUser user, float size, string reason, SocketTextChannel channel, string logLink = null) {
            try {
                if (user.CantBeWarned()) {
                    await channel.SendMessageAsync("This person can't be warned");
                    return;
                }

                await user.Id.Warn(size, reason, channel, user, logLink);
            } catch (Exception e) {
                await new LogMessage(LogSeverity.Error, "Warn", "An exception has happened while warning", e).Log();
            }
        }

        public static async Task Warn(this ulong userID, float size, string reason, SocketTextChannel channel, IUser warnee = null, string logLink = null) {
            if (size > 999 || size < 0.01) {
                await channel.SendMessageAsync("Why would you need to warn someone with that size?");
                return;
            }

            List<Infraction> infractions = userID.LoadInfractions(channel.Guild, true);
            Infraction newInfraction = new Infraction {
                reason = reason,
                time = DateTime.Now,
                size = size
            };
            if (!logLink.IsNullOrEmpty()) newInfraction.logLink = logLink;
            infractions.Add(newInfraction);
            userID.SaveInfractions(channel.Guild, infractions);

            try {
                if (warnee != null) {
                    IUser[] users = await (channel as ISocketMessageChannel).GetUsersAsync().Flatten().ToArray();
                    if (!users.Any(xUser => xUser.Id == userID)) {
                        warnee.TryNotify($"You have been warned in {channel.Guild.Name} discord for \"{reason}\" in a channel you can't view");
                    }
                }
            } catch {
            }
        }
        public struct InfractionsInDays {
            public float sum;
            public int count;
        }

        public struct InfractionInfo {
            public InfractionsInDays infractionsToday;
            public InfractionsInDays infractions30Days;
            public InfractionsInDays totalInfractions;
            public InfractionsInDays infractions7Days;
            public List<string> infractionStrings;
            public InfractionInfo(List<Infraction> infractions, int amount = 5, bool showLinks = false) {
                infractionsToday = new InfractionsInDays();
                infractions30Days = new InfractionsInDays();
                totalInfractions = new InfractionsInDays();
                infractions7Days = new InfractionsInDays();
                infractionStrings = new List<string> { "" };

                infractions.Reverse();
                if (infractions.Count < amount) {
                    amount = infractions.Count;
                }
                int n = 0;
                for (int i = 0; i < infractions.Count; i++) {
                    Infraction infraction = infractions[i];

                    //Gets how long ago all the infractions were
                    TimeSpan dateAgo = DateTime.Now.Subtract(infraction.time);
                    totalInfractions.sum += infraction.size;
                    totalInfractions.count++;
                    if (dateAgo.Days <= 7) {
                        infractions7Days.sum += infraction.size;
                        infractions7Days.count++;
                    }
                    if (dateAgo.Days <= 30) {
                        infractions30Days.sum += infraction.size;
                        infractions30Days.count++;
                        if (dateAgo.Days < 1) {
                            infractionsToday.sum += infraction.size;
                            infractionsToday.count++;
                        }
                    }

                    string size = "";
                    if (infraction.size != 1) {
                        size = "(" + infraction.size + "x) ";
                    }

                    if (n < amount) {
                        string jumpLink = "";
                        string timeAgo = dateAgo.LimitedHumanize(2);
                        if (showLinks && !infraction.logLink.IsNullOrEmpty()) jumpLink = $" [[Logged Here]({infraction.logLink})]";
                        string s = "[" + MathF.Abs(i - infractions.Count) + "] " + size + infraction.reason + jumpLink + " - " + timeAgo;
                        n++;

                        if ((infractionStrings.LastOrDefault() + s).Length < 1024) {
                            if (infractionStrings.LastOrDefault() != "") infractionStrings[infractionStrings.Count - 1] += "\n";
                            infractionStrings[infractionStrings.Count - 1] += s;
                        } else {
                            infractionStrings.Add(s);
                        }
                    }
                }
            }
        }

        public static Embed GetEmbed(this List<Infraction> infractions, SocketGuildUser user = null, int amount = 5, bool showLinks = false) {
            InfractionInfo data = new InfractionInfo(infractions, amount, showLinks);

            //Builds infraction embed
            var embed = new EmbedBuilder();
            embed.AddField("Today",
                $"{data.infractionsToday.sum} sum**|**{data.infractionsToday.count} count", true);
            embed.AddField("Last 7 days",
                $"{data.infractions7Days.sum} sum**|**{data.infractions7Days.count} count", true);
            embed.AddField("Last 30 days",
                $"{data.infractions30Days.sum} sum**|**{data.infractions30Days.count} count", true);
            embed.AddField("Warning".Pluralize(data.totalInfractions.count) + " (total " + data.totalInfractions.sum + " sum of size & " + infractions.Count + " individual)",
                data.infractionStrings[0]);
            data.infractionStrings.RemoveAt(0);
            foreach (string s in data.infractionStrings) {
                embed.AddField("------------------------------------------------------------", s);
            }
            if (user != null) {
                embed.WithAuthor(user)
                .WithFooter("ID: " + user.Id)
                .WithColor(Color.Blue)
                .WithCurrentTimestamp();
            }

            return embed.Build();
        }

        public static async Task TempBan(this SocketGuildUser user, TimeSpan time, string reason, SocketCommandContext context, TempActionList actions = null) {
            TempAct tempBan = new TempAct(user.Id, time, reason);
            if (actions == null) actions = context.Guild.LoadFromFile<TempActionList>(true);
            actions.tempBans.Add(tempBan);
            actions.SaveToFile(context.Guild);
            try {
                await user.Notify($"tempbanned for {time.LimitedHumanize()}", reason, context.Guild, context.Message.Author);
            } catch (Exception e) {
                if (e is NullReferenceException) await new LogMessage(LogSeverity.Error, "TempAct", "Something went wrong notifying person", e).Log();
            }
            await context.Guild.AddBanAsync(user, reason: reason);
            Logging.LogTempAct(context.Guild, context.User, user, "bann", reason, context.Message.GetJumpUrl(), time);
        }

        public static async Task TempMute(this SocketGuildUser user, TimeSpan time, string reason, SocketCommandContext context, ModerationSettings settings, TempActionList actions = null) {
            TempAct tempMute = new TempAct(user.Id, time, reason);
            if (actions == null) actions = context.Guild.LoadFromFile<TempActionList>(true);
            actions.tempMutes.Add(tempMute);
            actions.SaveToFile(context.Guild);
            try {
                await user.Notify($"tempmuted for {time.LimitedHumanize()}", reason, context.Guild, context.Message.Author);
            } catch (Exception e) {
                if (e is NullReferenceException) await new LogMessage(LogSeverity.Error, "TempAct", "Something went wrong notifying person", e).Log();
            }
            await user.AddRoleAsync(context.Guild.GetRole(settings.mutedRole));
            Logging.LogTempAct(context.Guild, context.User, user, "mut", reason, context.Message.GetJumpUrl(), time);
        }

        public static async Task Notify(this IUser user, string action, string reason, SocketGuild guild, SocketUser author = null) {
            var embed = new EmbedBuilder();
            embed.WithTitle($"You have been {action} from a discord guild");
            embed.AddField("Reason", reason, true);
            embed.AddField("Guild name", guild.Name, true);
            embed.WithCurrentTimestamp();
            if (author != null) embed.WithAuthor(author);
            user.TryNotify(embed.Build());
        }
    }

    [RequireContext(ContextType.Guild)]
    public class DiscordModModule : InteractiveBase<SocketCommandContext> {
        [Command("warn")]
        [CanWarn()]
        public async Task WarnUserAsync([RequireHierarchy] SocketGuildUser user, [Remainder] string reason = "Unspecified") {
            string jumpLink = Logging.LogWarn(Context.Guild, Context.Message.Author, user.Id, reason, Context.Message.GetJumpUrl());
            await user.Warn(1, reason, Context.Channel as SocketTextChannel, logLink: jumpLink);

            Context.Message.DeleteOrRespond($"{user.Mention} has gotten their {user.LoadInfractions().Count.Suffix()} infraction for {reason}", Context.Guild);
        }

        [Command("warn")]
        [CanWarn()]
        public async Task WarnWithSizeUserAsync([RequireHierarchy] SocketGuildUser user, float size, [Remainder] string reason = "Unspecified") {
            string jumpLink = Logging.LogWarn(Context.Guild, Context.Message.Author, user.Id, reason, Context.Message.GetJumpUrl());
            await user.Warn(size, reason, Context.Channel as SocketTextChannel, logLink: jumpLink);

            Context.Message.DeleteOrRespond($"{user.Mention} has gotten their {user.LoadInfractions().Count.Suffix()} infraction for {reason}", Context.Guild);
        }

        [Command("dmwarns")]
        [RequireContext(ContextType.Guild)]
        [Alias("dminfractions", "dmwarnings")]
        public async Task DMUserWarnsAsync(SocketGuildUser user = null, int amount = 50) {
            if (amount < 1) {
                await ReplyAsync("Why would you want to see that many infractions?");
                return;
            }

            if (user == null) {
                user = Context.Message.Author as SocketGuildUser;
            }
            string username;
            if (!user.Nickname.IsNullOrEmpty()) username = user.Nickname.StrippedOfPing();
            else username = user.Username.StrippedOfPing();

            List<Infraction> infractions = user.LoadInfractions(false);
            if (!infractions.IsNullOrEmpty()) {
                try {
                    await Context.Message.Author.GetOrCreateDMChannelAsync().Result.SendMessageAsync(embed: infractions.GetEmbed(user, amount: amount));
                } catch {
                    await ReplyAsync("Something went wrong DMing you their infractions. Check your privacy settings and make sure the amount isn't too high");
                    return;
                }
            } else {
                await ReplyAsync($"{user.NickOrUsername().StrippedOfPing()} has no infractions");
                return;
            }
            string quantity = "infraction".ToQuantity(infractions.Count);
            if (amount >= infractions.Count) await ReplyAsync($"DMed you {username}'s {quantity}");
            else await ReplyAsync($"DMed you {username}'s last {amount} out of {quantity}");
        }

        [Command("warns")]
        [RequireContext(ContextType.Guild)]
        [Alias("infractions", "warnings")]
        public async Task CheckUserWarnsAsync(SocketGuildUser user = null, int amount = 5) {
            if (user == null) {
                user = Context.Message.Author as SocketGuildUser;
            }
            if (!(Context.Message.Author as SocketGuildUser).CanWarn()) {
                await ReplyAsync("To avoid flood only people who can warn can use this command. Please use !dmwarns instead");
                return;
            }

            List<Infraction> infractions = user.LoadInfractions(false);
            if (infractions.IsNullOrEmpty()) {
                await ReplyAsync($"{user.NickOrUsername().StrippedOfPing()} has no infractions");
                return;
            }
            await ReplyAsync(embed: infractions.GetEmbed(user, amount: amount, showLinks: true));
        }

        [Command("removewarn")]
        [Alias("warnremove", "removewarning")]
        [HasAdmin()]
        public async Task RemoveWarnAsync([RequireHierarchy] SocketGuildUser user, int index) {
            List<Infraction> infractions = user.LoadInfractions();
            if (infractions.IsNullOrEmpty()) {
                await ReplyAsync("Infractions are null");
                return;
            }
            if (infractions.Count < index || index <= 0) {
                await ReplyAsync("Invalid infraction number");
                return;
            }
            string reason = infractions[index - 1].reason;
            infractions.RemoveAt(index - 1);

            user.SaveInfractions(infractions);
            user.TryNotify($"Your {index.Ordinalize()} warning in {Context.Guild.Name} discord for {reason} has been removed");
            await ReplyAsync("Removed " + user.Mention + "'s warning for " + reason);
        }

        [Command("kickwarn")]
        [Alias("warnkick", "warnandkick", "kickandwarn")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task KickAndWarn([RequireHierarchy] SocketGuildUser user, [Remainder] string reason = "Unspecified") {
            await user.Warn(1, reason, Context.Channel as SocketTextChannel, "Discord");

            _ = user.Notify("kicked", reason, Context.Guild, Context.Message.Author);
            await user.KickAsync(reason);
            Context.Message.DeleteOrRespond($"{user.Mention} has been kicked for {reason} ", Context.Guild);
        }

        [Command("kickwarn")]
        [Alias("warnkick", "warnandkick", "kickandwarn")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task KickAndWarn([RequireHierarchy] SocketGuildUser user, float size, [Remainder] string reason = "Unspecified") {
            await user.Warn(size, reason, Context.Channel as SocketTextChannel, "Discord");

            _ = user.Notify("kicked", reason, Context.Guild, Context.Message.Author);
            await user.KickAsync(reason);
            Context.Message.DeleteOrRespond($"{user.Mention} has been kicked for {reason} ", Context.Guild);
        }

        [Command("tempban")]
        [Alias("tban", "temp-ban")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempBanUser([RequireHierarchy] SocketGuildUser user, string time, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-ban for less than a minute");
                return;
            }
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>(false);
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            TempAct oldAct = actions.tempBans.FirstOrDefault(tempMute => tempMute.user == user.Id);
            if (oldAct != null) {
                if (!(Context.Message.Author as SocketGuildUser).HasAdmin() && (oldAct.length - (DateTime.Now - oldAct.dateBanned)) >= amount) {
                    await ReplyAsync($"{Context.User.Mention} please contact your admin(s) in order to shorten length of a punishment");
                    return;
                }
                IUserMessage query = await ReplyAsync(
                    $"{user.NickOrUsername().StrippedOfPing()} is already temp-banned for {oldAct.length.LimitedHumanize()} ({(oldAct.length - (DateTime.Now - oldAct.dateBanned)).LimitedHumanize()} left), reply with !confirm within 2 minutes to confirm you want to change the length");
                SocketMessage nextMessage = await NextMessageAsync(timeout: TimeSpan.FromMinutes(2));
                if (nextMessage?.Content?.ToLower() == "!confirm") {
                    _ = query.DeleteAsync();
                    _ = nextMessage.DeleteAsync();
                    actions.tempBans.Remove(oldAct);
                    actions.SaveToFile(Context.Guild);
                } else {
                    _ = query.DeleteAsync();
                    if (nextMessage != null) _ = nextMessage.DeleteAsync();
                    await ReplyAsync("Command canceled");
                    return;
                }
            }
            await user.TempBan(amount.Value, reason, Context, actions);
            Context.Message.DeleteOrRespond($"Temporarily banned {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("tempbanwarn")]
        [Alias("tbanwarn", "temp-banwarn", "tempbanandwarn")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempBanWarnUser([RequireHierarchy] SocketGuildUser user, string time, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-ban for less than a minute");
                return;
            }
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>(false);
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            await user.Warn(1, reason, Context.Channel as SocketTextChannel, "Discord");
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            if (actions.tempBans.Any(tempBan => tempBan.user == user.Id)) {
                Context.Message.DeleteOrRespond($"{user.NickOrUsername().StrippedOfPing()} is already temp-banned (the warn did go through)", Context.Guild);
                return;
            }
            await user.TempBan(amount.Value, reason, Context, actions);
            Context.Message.DeleteOrRespond($"Temporarily banned {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("tempbanwarn")]
        [Alias("tbanwarn", "temp-banwarn", "tempbanwarn", "warntempban")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempBanWarnUser([RequireHierarchy] SocketGuildUser user, string time, float size, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-ban for less than a minute");
                return;
            }
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>(false);
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            await user.Warn(size, reason, Context.Channel as SocketTextChannel, "Discord");
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            if (actions.tempBans.Any(tempBan => tempBan.user == user.Id)) {
                await ReplyAsync($"{user.NickOrUsername().StrippedOfPing()} is already temp-banned (the warn did go through)");
                return;
            }
            await user.TempBan(amount.Value, reason, Context, actions);
            Context.Message.DeleteOrRespond($"Temporarily banned {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("tempmute", RunMode = RunMode.Async)]
        [Alias("tmute", "temp-mute")]
        [RequireBotPermission(GuildPermission.ManageRoles)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempMuteUser([RequireHierarchy] SocketGuildUser user, string time, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-mute for less than a minute");
                return;
            }
            ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>();
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            if (settings == null || settings.mutedRole == 0 || Context.Guild.GetRole(settings.mutedRole) == null) {
                await ReplyAsync("Muted role is null or invalid");
                return;
            }
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            TempAct oldAct = actions.tempMutes.FirstOrDefault(tempMute => tempMute.user == user.Id);
            if (oldAct != null) {
                if (!(Context.Message.Author as SocketGuildUser).HasAdmin() && (oldAct.length - (DateTime.Now - oldAct.dateBanned)) >= amount) {
                    await ReplyAsync($"{Context.User.Mention} please contact your admin(s) in order to shorten length of a punishment");
                    return;
                }
                IUserMessage query = await ReplyAsync(
                    $"{user.NickOrUsername().StrippedOfPing()} is already temp-muted for {oldAct.length.LimitedHumanize()} ({(oldAct.length - (DateTime.Now - oldAct.dateBanned)).LimitedHumanize()} left), reply with !confirm within 2 minutes to confirm you want to change the length");
                SocketMessage nextMessage = await NextMessageAsync(timeout: TimeSpan.FromMinutes(2));
                if (nextMessage?.Content?.ToLower() == "!confirm") {
                    _ = query.DeleteAsync();
                    _ = nextMessage.DeleteAsync();
                    actions.tempMutes.Remove(oldAct);
                    actions.SaveToFile(Context.Guild);
                } else {
                    _ = query.DeleteAsync();
                    if (nextMessage != null) _ = nextMessage.DeleteAsync();
                    await ReplyAsync("Command canceled");
                    return;
                }
            }

            await user.TempMute(amount.Value, reason, Context, settings, actions);
            Context.Message.DeleteOrRespond($"Temporarily muted {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("tempmutewarn")]
        [Alias("tmutewarn", "temp-mutewarn", "warntmute", "tempmuteandwarn")]
        [RequireBotPermission(GuildPermission.ManageRoles)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempMuteWarnUser([RequireHierarchy] SocketGuildUser user, string time, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-mute for less than a minute");
                return;
            }
            ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>();
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            if (settings == null || settings.mutedRole == 0 || Context.Guild.GetRole(settings.mutedRole) == null) {
                await ReplyAsync("Muted role is null or invalid");
                return;
            }
            await user.Warn(1, reason, Context.Channel as SocketTextChannel, "Discord");
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            if (actions.tempMutes.Any(tempMute => tempMute.user == user.Id)) {
                await ReplyAsync($"{user.NickOrUsername().StrippedOfPing()} is already temp-muted, (the warn did go through)");
                return;
            }

            await user.TempMute(amount.Value, reason, Context, settings, actions);
            Context.Message.DeleteOrRespond($"Temporarily muted {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("tempmutewarn")]
        [Alias("tmutewarn", "temp-mutewarn", "warntmute", "tempmuteandwarn")]
        [RequireBotPermission(GuildPermission.ManageRoles)]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task TempMuteWarnUser([RequireHierarchy] SocketGuildUser user, string time, float size, [Remainder] string reason) {
            var amount = time.ToTime();
            if (amount == null) {
                await ReplyAsync($"Unable to parse '{time}', be careful with decimals");
                return;
            }
            if (amount.Value.TotalMinutes < 1) {
                await ReplyAsync("Can't temp-mute for less than a minute");
                return;
            }
            ModerationSettings settings = Context.Guild.LoadFromFile<ModerationSettings>();
            if (!(Context.Message.Author as SocketGuildUser).HasAdmin()) {
                if (settings?.maxTempAction != null && amount > settings.maxTempAction) {
                    await ReplyAsync("You are not allowed to punish for that long");
                    return;
                }
            }
            if (settings == null || settings.mutedRole == 0 || Context.Guild.GetRole(settings.mutedRole) == null) {
                await ReplyAsync("Muted role is null or invalid");
                return;
            }
            await user.Warn(size, reason, Context.Channel as SocketTextChannel, "Discord");
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(true);
            if (actions.tempMutes.Any(tempMute => tempMute.user == user.Id)) {
                await ReplyAsync($"{user.NickOrUsername().StrippedOfPing()} is already temp-muted, (the warn did go through)");
                return;
            }

            await user.TempMute(amount.Value, reason, Context, settings, actions);
            Context.Message.DeleteOrRespond($"Temporarily muted {user.Mention} for {amount.Value.LimitedHumanize(3)} because of {reason}", Context.Guild);
        }

        [Command("ban")]
        [RequireBotPermission(GuildPermission.BanMembers)]
        [RequireUserPermission(GuildPermission.BanMembers)]
        public async Task Ban(SocketUser user, [Remainder] string reason = "Unspecified") {
            TempActionList actions = Context.Guild.LoadFromFile<TempActionList>(false);
            if (actions?.tempBans?.Any(tempBan => tempBan.user == user.Id) ?? false) {
                actions.tempBans.Remove(actions.tempBans.First(tempban => tempban.user == user.Id));
            } else if (Context.Guild.GetBansAsync().Result.Any(ban => ban.User.Id == user.Id)) {
                await ReplyAsync("User has already been banned permanently");
                return;
            }
            user.TryNotify($"You have been banned in the {Context.Guild.Name} discord for {reason}");
            await Context.Guild.AddBanAsync(user);
            Context.Message.DeleteOrRespond($"User has been banned for {reason}", Context.Guild);
        }
    }
}