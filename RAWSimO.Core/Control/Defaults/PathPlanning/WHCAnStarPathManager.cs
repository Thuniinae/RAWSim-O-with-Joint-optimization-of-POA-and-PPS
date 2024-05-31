using RAWSimO.Core.Bots;
using RAWSimO.Core.Configurations;
using RAWSimO.Core.Elements;
using RAWSimO.Core.Helper;
using RAWSimO.Core.Interfaces;
using RAWSimO.Core.Metrics;
using RAWSimO.Core.Waypoints;
using RAWSimO.MultiAgentPathFinding;
using RAWSimO.MultiAgentPathFinding.Elements;
using RAWSimO.MultiAgentPathFinding.Methods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RAWSimO.Core.Control.Defaults.PathPlanning
{

    /// <summary>
    /// Controller of the bot.
    /// </summary>
    public class WHCAnStarPathManager : PathManager
    {

        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="instance">instance</param>
        public WHCAnStarPathManager(Instance instance)
            : base(instance)
        {
            //Need a Request on Fail
            BotNormal.RequestReoptimizationAfterFailingOfNextWaypointReservation = true;

            //translate to lightweight graph
            var graph = GenerateGraph();
            var config = instance.ControllerConfig.PathPlanningConfig as WHCAnStarPathPlanningConfiguration;

            PathFinder = new WHCAnStarMethod(graph, instance.SettingConfig.Seed, instance.Bots.Select(b => b.ID).ToList(), instance.Bots.Select(b => _waypointIds[instance.WaypointGraph.GetClosestWaypoint(b.Tier, b.X, b.Y)]).ToList(), new PathPlanningCommunicator(
                instance.LogSevere,
                instance.LogDefault,
                instance.LogInfo,
                instance.LogVerbose,
                () => { instance.StatOverallPathPlanningTimeouts++; }));
            var method = PathFinder as WHCAnStarMethod;
            method.LengthOfAWaitStep = config.LengthOfAWaitStep;
            method.RuntimeLimitPerAgent = config.RuntimeLimitPerAgent;
            method.RunTimeLimitOverall = config.RunTimeLimitOverall;
            method.LengthOfAWindow = config.LengthOfAWindow;
            method.UseBias = config.UseBias;
            method.UseDeadlockHandler = config.UseDeadlockHandler;

            if (config.AutoSetParameter)
            {
                //best parameter determined my master thesis
                method.LengthOfAWindow = 15;
                method.UseBias = false;
                method.RuntimeLimitPerAgent = config.Clocking / instance.Bots.Count;
                method.RunTimeLimitOverall = config.Clocking;
            }
        }
        /// <summary>
        /// Estimate ending time of a bot, using WHCA* reservation table.
        /// </summary>
        /// <param name="endTime">CurrentTime + Time cost in window(may be different when planed) + Time cost outside the window (estimated)</param>
        /// <param name="bot"></param>
        /// <param name="currentTime"></param>
        /// <param name="startWaypoint"></param>
        /// <param name="endWaypoint"></param>
        /// <param name="carryingPod"></param>
        /// <returns>false, if no path can be found.</returns>
        override public bool findPath(out double endTime, double currentTime,
            Bot bot, Waypoint startWaypoint, Waypoint endWaypoint, bool carryingPod)
        {
            Agent agent;
            getBotAgent(out agent, bot, currentTime, startWaypoint, endWaypoint, carryingPod);
            var method = PathFinder as WHCAnStarMethod;
            var success = method.findPath(out endTime, currentTime, agent);
            if (success){
                // estimated travel time of path outside of WHCA* window
                var waypoint = bot.Instance.Controller.PathManager.GetWaypointByNodeId(agent.Path.LastAction.Node);
                if(carryingPod)
                    endTime += Distances.CalculateShortestTimePathPodSafe(waypoint, endWaypoint, Instance);
                else
                    endTime += Distances.CalculateShortestTimePath(waypoint, endWaypoint, Instance);
                // TODO: add penalty for possible collision
            }
            return success;
        }
    }
}
