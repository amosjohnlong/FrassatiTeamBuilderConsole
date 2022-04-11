using FrassatiTeamBuilderConsole.ExtensionMethods;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace FrassatiTeamBuilderConsole
{
    public static class Program
    {
        private static string configFilePath = @"C:\Users\amos.long\source\repos\FrassatiTeamBuilderConsole\config.cfg";
        private static string playerDataFilePath = @"C:\Users\amos.long\source\repos\FrassatiTeamBuilderConsole\Data\TestData\PlayerData.csv";
        private static string outputFilePath = @"C:\Users\amos.long\source\repos\FrassatiTeamBuilderConsole\Data\OutputData\output.csv";
        private static string outputFolderPath = @"C:\Users\amos.long\source\repos\FrassatiTeamBuilderConsole\Data\OutputData\";

        public static void Main(string[] args)
        {
            Console.WriteLine("Frassati Team Builder");

            var configSettings = ReadConfigFile(configFilePath);

            var players = ReadInputCSV(playerDataFilePath);
            players = Player.FindAllGroupData(players);

            int teamCount = players.Count(p => p.IsCaptain);
            int teamSize = players.Length / teamCount + ((players.Length % teamCount > 0) ? 1 : 0);

            //      b: Determine evaluation metrics
            var metrics = GenerateMetricsAlgorithms(configSettings.Metrics);
            EvaluateLeagueWideMetrics(ref metrics, players);

            var teamPossibilities = GenerateValidRosterPossibilities(players, teamCount, teamSize,
                                        configSettings.Metrics, metrics, configSettings.MetricEvaluationThreshold, configSettings.NumberOfRostersToOutput);

        }

        public static ConfigSettings ReadConfigFile(string filePath)
        {
            if (!File.Exists(filePath)) throw new Exception($"Config File does not exist: {filePath}");

            ConfigSettings settings = JsonConvert.DeserializeObject<ConfigSettings>(File.ReadAllText(filePath));

            return settings;
        }

        public static Player[] ReadInputCSV(string filePath)
        {
            if (!File.Exists(filePath)) throw new Exception($"Input File does not exist: {filePath}");

            string[] fileLines = File.ReadAllLines(filePath);

            Player[] playerArray = fileLines.Skip(1).Select(l => new Player(l)).ToArray();
            return playerArray;
        }

        private static Func<Player[], double> AveragePlayerRatingFunc = players => players.Where(p => p != null).Average(p => p.Rating);
        private static Func<Player[], double> AverageMaleRatingFunc = players => players.Where(p => p != null && p.Gender == "Male").AverageOrZero(p => p.Rating);
        private static Func<Player[], double> AverageFemaleRatingFunc = players => players.Where(p => p != null && p.Gender == "Female").AverageOrZero(p => p.Rating);
        private static Func<Player[], double> Top5TotalFunc = players => players.Where(p => p != null).OrderByDescending(p => p.Rating).Take(5).Sum(p => p.Rating);

        private static readonly Dictionary<string, Func<Player[], double>> MetricFunctionLookup = new() {
            { "AveragePlayerRating", AveragePlayerRatingFunc },
            { "AverageMaleRating", AverageMaleRatingFunc }, 
            { "AverageFemaleRating", AverageFemaleRatingFunc}, 
            { "Top5Total", Top5TotalFunc } };

        public static Metric[] GenerateMetricsAlgorithms(string[] metricsNames)
        {
            foreach(string metricName in metricsNames)
            {
                if (!MetricFunctionLookup.ContainsKey(metricName)) throw new ArgumentException($"{metricName} in config file is not implemented");
            }

            return metricsNames.Select(n => new Metric() { MetricName = n, MetricAlgorithm = MetricFunctionLookup[n] }).ToArray();
        }

        public static void EvaluateLeagueWideMetrics(ref Metric[] metricsArray, Player[] playerArray)
        {
            metricsArray.ForEach(m => m.LeagueWideMetric = m.MetricAlgorithm(playerArray));
        }

        public static Roster[] GenerateValidRosterPossibilities(Player[] allPlayers, int teamCount, int teamSize, 
            string[] metricsNames, Metric[] metrics, float metricsThreshold, int rosterPossibilitiesToGenerate)
        {
            double leagueAverageRating = allPlayers.Average(p => p.Rating);
            List<Player> captainGroup = allPlayers.Where(p => p.IsCaptain || (p.GroupID.HasValue && 
                                                            allPlayers.Any(c => c.IsCaptain && c.GroupID.HasValue && c.GroupID.Value == p.GroupID.Value))).ToList();

            int evaluationSetIndex = 1;
            while (evaluationSetIndex <= rosterPossibilitiesToGenerate)
            {
                Roster evaluationSet = InitializeTeamsAndCaptains(allPlayers, teamCount, teamSize, metrics.Length);
                evaluationSet.EvaluationSetID = evaluationSetIndex;

                List<Player> availablePlayers = allPlayers.Except(captainGroup).ToList();

                bool hasError = AssignTeamMembersUsingRandomStart(evaluationSet, availablePlayers, leagueAverageRating);

                bool areTeamMetricsWithinThreshold = EvaluateMetrics(evaluationSet, metrics, metricsThreshold);

                if (!hasError && areTeamMetricsWithinThreshold)
                {
                    //evaluationSet.PrintMemberIDsToCSV(outputFilePath);
                    evaluationSet.PrintRosterToCSV(outputFolderPath + $"Roster{evaluationSet.EvaluationSetID}.csv", metricsNames);
                    evaluationSetIndex++;
                }
            }

            return new Roster[0];
        }

        private static Roster InitializeTeamsAndCaptains(Player[] allPlayers, int teamCount, int teamSize, int metricsCount)
        {
            Roster evaluationSetTemplate = new(teamCount, metricsCount);

            for (int teamIndex = 0; teamIndex < teamCount; teamIndex++)
            {
                Player captain = allPlayers.Where(p => p.IsCaptain).OrderBy(p => p.PlayerID).Skip(teamIndex).First();

                Team team = new(teamIndex, teamSize, captain.PlayerName, metricsCount);
                team.AddTeamMember(captain, allPlayers.ToList());

                evaluationSetTemplate.Teams[teamIndex] = team;
            }

            return evaluationSetTemplate;
        }

        private static bool AssignTeamMembersUsingRandomStart(Roster evaluationSet, List<Player> availablePlayers, double leagueAverageRating)
        {
            AssignOneRandomPlayerToEachTeam(ref evaluationSet, ref availablePlayers);

            bool hasError = AssignPlayersToTeamUsingAverages(evaluationSet, availablePlayers, leagueAverageRating, 
                                        GetHighestRatedPlayer_GroupsFirst, GetLowestRatedPlayer_GroupsFirst);
            return hasError;
        }

        // TODO: Create a AssignTeamMemberUsingFemaleStart()

        private static void AssignOneRandomPlayerToEachTeam(ref Roster evaluationSet, ref List<Player> availablePlayers)
        {
            for (int index = 0; index < evaluationSet.Teams.Length; index++)
            {
                PlaceRandomPlayer(ref evaluationSet.Teams[index], ref availablePlayers);
            }    
        }

        private static bool AssignPlayersToTeamUsingAverages(Roster evaluationSet, List<Player> availablePlayers, double leagueAverageRating, 
                                                                Func<List<Player>, Player> GetHighestRatedPlayer, Func<List<Player>, Player> GetLowestRatedPlayer)
        {
            bool hasError = false;

            // Assign players to teams until all players are gone
            while (!hasError && availablePlayers.Count > 0)
            {
                // If team's rating is below average, add highest rated player
                var belowAverageTeams = evaluationSet.Teams.Where(t => t.AverageMemberRating < leagueAverageRating).OrderBy(t => t.AverageMemberRating);

                foreach (Team team in belowAverageTeams)
                {
                    if (availablePlayers.Count == 0) break;

                    if (!team.IsFull() && team.AverageMemberRating < leagueAverageRating)
                    {
                        Player highestRatedPlayer = GetHighestRatedPlayer(availablePlayers);
                        hasError |= team.AddTeamMember(highestRatedPlayer, availablePlayers);
                        SetPlayerOrGroupAsNotAvailable(highestRatedPlayer, ref availablePlayers);
                    }
                }

                // If team's rating is above average, add lowest rated player
                var aboveAverageTeams = evaluationSet.Teams.Where(t => t.AverageMemberRating >= leagueAverageRating).OrderByDescending(t => t.AverageMemberRating);

                foreach (Team team in aboveAverageTeams)
                {
                    if (availablePlayers.Count == 0) break;

                    if (!team.IsFull() && team.AverageMemberRating >= leagueAverageRating)
                    {
                        Player lowestRatedPlayer = GetLowestRatedPlayer(availablePlayers);
                        hasError |= team.AddTeamMember(lowestRatedPlayer, availablePlayers);
                        SetPlayerOrGroupAsNotAvailable(lowestRatedPlayer, ref availablePlayers);
                    }
                }
            }

            return hasError;
        }

        private static bool EvaluateMetrics(Roster evaluationSet, Metric[] metrics, double metricsThreshold)
        {
            for (int teamIndex = 0; teamIndex < evaluationSet.Teams.Length; teamIndex++)
            {
                for (int metricIndex = 0; metricIndex < metrics.Length; metricIndex++)
                {
                    evaluationSet.Teams[teamIndex].TeamMetrics[metricIndex] = metrics[metricIndex].MetricAlgorithm(evaluationSet.Teams[teamIndex].TeamMembers);
                }
            }

            bool areTeamMetricsWithinThreshold = evaluationSet.Teams.SelectMany(t => t.TeamMetrics.Select((m, i) => Math.Abs(m - metrics[i].LeagueWideMetric.Value)))
                                                            .All(x => x < metricsThreshold);

            for (int index = 0; index < evaluationSet.TeamMetricsRSME.Length; index++)
            {
                evaluationSet.TeamMetricsRSME[index] = CalculateRMSE(evaluationSet.Teams.Select(t => t.TeamMetrics[index]));
            }

            return areTeamMetricsWithinThreshold;
        }

        private static double CalculateRMSE(IEnumerable<double> arr)
        {
            double average = arr.AverageOrZero(e => e);
            return Math.Sqrt(arr.Sum(e => Square(e - average)) / arr.Count());
        }

        private static readonly Func<double, double> Square = d => d * d;

        private static void SetPlayerOrGroupAsNotAvailable(Player playerToMove, ref List<Player> availablePlayers)
        {
            if (playerToMove.GroupID.HasValue)
            {
                var allPlayersInGroup = availablePlayers.Where(p => playerToMove.AllGroupMemberIDs.Contains(p.PlayerID)).ToList();
                availablePlayers = availablePlayers.Except(allPlayersInGroup).ToList();
            }
            else
            {
                availablePlayers.Remove(playerToMove);
            }
        }

        private static void PlaceRandomPlayer(ref Team team, ref List<Player> availablePlayers)
        {
            int availablePlayerCount = availablePlayers.Count;

            Random rnd = new();
            int randomPlayerIndex = rnd.Next(availablePlayerCount);
            Player randomPlayer = availablePlayers[randomPlayerIndex];

            team.AddTeamMember(randomPlayer, availablePlayers);
            SetPlayerOrGroupAsNotAvailable(randomPlayer, ref availablePlayers);
        }

        private static Player GetHighestRatedPlayer_GroupsFirst(List<Player> availablePlayers)
        {
            var playersInGroups = availablePlayers.Where(p => p.GroupID.HasValue).ToList();

            Player highestRatedPlayer = (playersInGroups.Count() > 0) ?
                playersInGroups.OrderByDescending(p => p.GroupAverageRating).First() :
                availablePlayers.Where(p => !p.GroupID.HasValue).OrderByDescending(p => p.Rating).FirstOrDefault();

            return highestRatedPlayer;
        }

        private static Player GetLowestRatedPlayer_GroupsFirst(List<Player> availablePlayers)
        {
            //if (availablePlayers.Count == 0) return null;

            var playersInGroups = availablePlayers.Where(p => p.GroupID.HasValue).ToList();

            Player lowestRatedPlayer = (playersInGroups.Count() > 0) ?
                playersInGroups.OrderBy(p => p.GroupAverageRating).First() :
                availablePlayers.Where(p => !p.GroupID.HasValue).OrderBy(p => p.Rating).FirstOrDefault();

            return lowestRatedPlayer;
        }
    }

    public class ConfigSettings
    {
        public int NumberOfRostersToOutput;
        public string[] Metrics;
        public float MetricEvaluationThreshold;
    }

    public class Roster
    {
        public int EvaluationSetID;
        public Team[] Teams;
        public double[] TeamMetricsRSME;

        public Roster(int teamCount, int metricsCount) 
        { 
            Teams = new Team[teamCount];
            TeamMetricsRSME = new double[metricsCount]; 
        }

        public void PrintMemberIDsToCSV(string CSVPath)
        {
            StringBuilder builder = new();

            builder.Append($"{EvaluationSetID}:[");
            Teams.ForEach(t => builder.Append($"[{string.Join(",", t.TeamMemberIDs)}],"));
            builder.AppendLine("]");

            File.AppendAllText(CSVPath, builder.ToString());
        }

        public void PrintRosterToCSV(string CSVPath, string[] metricsNames)
        {
            StringBuilder builder = new();

            builder.Append(",");
            foreach (var team in Teams)
            {
                builder.Append($"Team {team.TeamID},");
            }
            builder.AppendLine();

            for (int i = 0; i < Teams[0].TeamMembers.Length; i++)
            {
                builder.Append(i == 0 ? "Captain," : $"Round {i},");
                foreach (var team in Teams)
                {
                    var member = team.TeamMembers[i];
                    builder.Append(member == null ? "," : $"{member.PlayerName} ({member.Rating}),");
                }
                builder.AppendLine();
            }

            builder.Append(',', Teams.Length + 1);
            builder.AppendLine();

            builder.Append("Rating Total,");
            foreach (var team in Teams)
            {
                builder.Append($"{team.TeamMembers.Where(m => m != null).Sum(m => m.Rating)},");
            }
            builder.AppendLine();

            builder.Append("Average Rating,");
            foreach (var team in Teams)
            {
                builder.Append($"{team.AverageMemberRating},");
            }
            builder.AppendLine($"{Teams.SelectMany(t => t.TeamMembers.Where(p => p != null).Select(p => p.Rating)).Average(r => r)}");

            builder.Append(',', Teams.Length + 1);
            builder.AppendLine("RMSE");

            for (int i = 0; i < TeamMetricsRSME.Length; i++)
            {
                builder.Append($"{metricsNames[i]},");
                foreach (var team in Teams)
                {
                    builder.Append($"{team.TeamMetrics[i]},");
                }
                builder.AppendLine($"{TeamMetricsRSME[i]}");
            }

            File.WriteAllText(CSVPath, builder.ToString());
        }
    }

    public class Metric
    {
        public string MetricName = string.Empty;
        public double? LeagueWideMetric = null;
        public Func<Player[], double> MetricAlgorithm = (x) => { return -1f; };
    }

    public class Team
    {
        public int TeamID = -1;
        public string CaptainName = string.Empty;
        public Player[] TeamMembers;
        public int?[] TeamMemberIDs;
        public double[] TeamMetrics;
        public double AverageMemberRating;

        private int TeamMemberCount = 0;

        public Team(int teamID, int teamSize, string captainName, int metricsCount)
        {
            TeamID = teamID;
            TeamMembers = new Player[teamSize];
            TeamMemberIDs = new int?[teamSize];
            CaptainName = captainName;
            TeamMetrics = new double[metricsCount];
        }

        public bool AddTeamMember(Player newMember, List<Player> availablePlayers)
        {
            bool hasError = true;

            if (!newMember.GroupID.HasValue && TeamMemberCount < TeamMembers.Length)
            { 
                TeamMembers[TeamMemberCount] = newMember;
                TeamMemberIDs[TeamMemberCount] = newMember.PlayerID;

                TeamMemberCount++;
                AverageMemberRating = TeamMembers.Where(m => m != null).Average(m => m.Rating);

                hasError = false;
            }
            else if (newMember.GroupID.HasValue && (TeamMemberCount + newMember.AllGroupMemberIDs.Length - 1) < TeamMembers.Length)
            {
                Player[] newMembers = availablePlayers.Where(p => newMember.AllGroupMemberIDs.Contains(p.PlayerID)).ToArray();
                Array.Copy(newMembers, 0, TeamMembers, TeamMemberCount, newMember.AllGroupMemberIDs.Length);

                Array.Copy(newMember.AllGroupMemberIDs.Cast<int?>().ToArray(), 0, TeamMemberIDs, TeamMemberCount, newMember.AllGroupMemberIDs.Length);

                TeamMemberCount += newMember.AllGroupMemberIDs.Length;
                AverageMemberRating = TeamMembers.Where(m => m != null).Average(m => m.Rating);

                hasError = false;
            }

            return hasError;
        }

        public bool IsFull()
        {
            return TeamMemberCount == TeamMembers.Length;
        }

        public int GetFemaleCount()
        {
            return TeamMembers.Count(m => m != null && m.Gender == "Female");
        }
    }

    public class Player
    {
        public int PlayerID  = -1;
        public string PlayerName = string.Empty;
        public bool IsCaptain = false;
        public string Gender = string.Empty;
        public double Rating = -1;
        public int? GroupID = null;       // This is a unique ID for each pair/trio that signs up together.  This is not a reference to another player's PlayerID.
        public double GroupAverageRating;
        public int[] AllGroupMemberIDs = null;

        public Player(int playerID, string playerName, bool isCaptain, string gender, double rating, int? groupID)
        {
            PlayerID = playerID;
            PlayerName = playerName;
            IsCaptain = isCaptain;
            Gender = gender;
            Rating = rating;
            GroupID = groupID;
        }

        public Player(string oneLine)
        {
            var fields = oneLine.Split(",");
            PlayerID = Convert.ToInt32(fields[0]);
            PlayerName = fields[1];
            IsCaptain = (fields[2] == "Yes");
            Gender = fields[3];
            Rating = Convert.ToDouble(fields[4]);
            GroupID =  (string.IsNullOrEmpty(fields[5])) ? null : Convert.ToInt32(fields[5]);
        }

        public static Player[] FindAllGroupData(Player[] playerArray)
        {
            var playersInGroups = playerArray.Where(p => p.GroupID.HasValue);
            var groups = playersInGroups.GroupBy(p => p.GroupID, (key, g) => new { GroupID = key, GroupAverageRating = g.Average(m => m.Rating), AllGroupMemberIDs = g.Select(m => m.PlayerID).ToArray() });

            foreach(var group in groups)
            {
                foreach(var player in playerArray)
                {
                    if (player.GroupID == group.GroupID)
                    {
                        player.GroupAverageRating = group.GroupAverageRating;
                        player.AllGroupMemberIDs = group.AllGroupMemberIDs;
                    }
                }

            }

            return playerArray;
        }
    }
}
