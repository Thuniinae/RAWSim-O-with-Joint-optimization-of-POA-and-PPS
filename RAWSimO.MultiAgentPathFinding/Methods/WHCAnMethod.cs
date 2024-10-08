using RAWSimO.MultiAgentPathFinding.Algorithms.AStar;
using RAWSimO.MultiAgentPathFinding.DataStructures;
using RAWSimO.MultiAgentPathFinding.Elements;
using RAWSimO.MultiAgentPathFinding.Physic;
using RAWSimO.MultiAgentPathFinding.Toolbox;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace RAWSimO.MultiAgentPathFinding.Methods
{
    /// <summary>
    /// A* Approach for the Multi Agent Path Finding Problem
    /// </summary>
    public class WHCAnStarMethod : PathFinder
    {

        /// <summary>
        /// The length of a wait step
        /// </summary>
        public double LengthOfAWindow = 15.0;

        /// <summary>
        /// The RRA* Searches
        /// </summary>
        public Dictionary<int, ReverseResumableAStar> rraStars;

        /// <summary>
        /// Indicates whether the method uses the biased cost pathfinding algorithm
        /// </summary>
        public bool UseBias = false;

        /// <summary>
        /// Indicates whether the method uses a deadlock handler.
        /// </summary>
        public bool UseDeadlockHandler = true;

        /// <summary>
        /// Reservation Table
        /// </summary>
        public ReservationTable _reservationTable;

        public ReservationTable scheduledTable {get; private set;}

        public Dictionary<int, List<ReservationTable.Interval>> scheduledPath {get; private set;}

        /// <summary>
        /// Sequence (latest to earliest) of the scheduled path.
        /// </summary>
        public List<int> scheduleSequence {get; private set;}
        public Dictionary<int, int> AgentPriorities {get; private set;}

        /// <summary>
        /// The calculated reservations
        /// </summary>
        private Dictionary<int, List<ReservationTable.Interval>> _calculatedReservations;

        /// <summary>
        /// The deadlock handler
        /// </summary>
        private DeadlockHandler _deadlockHandler;

        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="graph">graph</param>
        /// <param name="seed">The seed to use for the randomizer.</param>
        /// <param name="logger">The logger to use.</param>
        public WHCAnStarMethod(Graph graph, int seed, List<int> agentIds, List<int> startIds, PathPlanningCommunicator logger)
            : base(graph, seed, logger)
        {
            if (graph.BackwardEdges == null)
                graph.GenerateBackwardEgdes();
            rraStars = new Dictionary<int, ReverseResumableAStar>();
            _reservationTable = new ReservationTable(graph, true, false, false);


            _calculatedReservations = new Dictionary<int, List<ReservationTable.Interval>>();
            for (var i = 0; i < agentIds.Count; i++)
            {
                _calculatedReservations.Add(agentIds[i], new List<ReservationTable.Interval>(new ReservationTable.Interval[] { new ReservationTable.Interval(startIds[i], 0, double.PositiveInfinity) }));
                _reservationTable.Add(_calculatedReservations[agentIds[i]]);
            }
            if (UseDeadlockHandler)
                _deadlockHandler = new DeadlockHandler(graph, seed);
            AgentPriorities = new();
        }

        /// <summary>
        /// Find the path for all the agents.
        /// </summary>
        /// <param name="agents">agents</param>
        /// <param name="queues">The queues for the destination, starting with the destination point.</param>
        /// <param name="nextReoptimization">The next re-optimization time.</param>
        /// <returns>
        /// paths
        /// </returns>
        /// <exception cref="System.Exception">Here I have to do something</exception>
        public override void FindPaths(double currentTime, List<Agent> agents)
        {
            Stopwatch.Restart();

            //Reservation Table
            _reservationTable.Reorganize(currentTime);

            //remove all agents that are not affected by this search
            foreach (var missingAgentId in _calculatedReservations.Keys.Where(id => !agents.Any(a => a.ID == id)))
                _reservationTable.Remove(_calculatedReservations[missingAgentId]);

            //sort Agents
            //agents = agents.OrderBy(a => Graph.getDistance(a.NextNode, a.DestinationNode)).ToList();
            agents = agents.OrderByDescending(a => AgentPriorities.ContainsKey(a.ID)? AgentPriorities[a.ID]: 0).ThenBy(a => a.CanGoThroughObstacles ? 1 : 0).ThenBy(a => Graph.getDistance(a.NextNode, a.DestinationNode)).ToList();
            Dictionary<int, double> bias = new Dictionary<int, double>();

            //deadlock handling
            if (UseDeadlockHandler)
            {
                _deadlockHandler.LengthOfAWaitStep = LengthOfAWaitStep;
                _deadlockHandler.MaximumWaitTime = 30;
                _deadlockHandler.Update(agents, currentTime);
            }

            //optimize Path
            if (UseBias)
            {
                foreach (var agent in agents.Where(a => a.RequestReoptimization && !a.FixedPosition && a.NextNode != a.DestinationNode))
                {
                    //Create RRA* search if necessary.
                    //Necessary if the agent has none or the agents destination has changed
                    ReverseResumableAStar rraStar;
                    if (!rraStars.TryGetValue(agent.ID, out rraStar) || rraStar == null || rraStar.StartNode != agent.DestinationNode ||
                        UseDeadlockHandler && _deadlockHandler.IsInDeadlock(agent, currentTime)) // TODO this last expression is used to set back the state of the RRA* in case of a deadlock - this is only a hotfix
                        rraStars[agent.ID] = new ReverseResumableAStar(Graph, agent, agent.Physics, agent.DestinationNode);

                    if (rraStars[agent.ID].Closed.Contains(agent.NextNode) || rraStars[agent.ID].Search(agent.NextNode))
                    {
                        var nodes = rraStars[agent.ID].getPathAsNodeList(agent.NextNode);
                        nodes.Add(agent.NextNode);
                        foreach (var node in nodes)
                        {
                            if (!bias.ContainsKey(node))
                                bias.Add(node, 0.0);
                            bias[node] += LengthOfAWaitStep * 1.0001;
                        }
                    }

                }
            }

            //optimize Path
            foreach (var agent in agents.Where(a => a.RequestReoptimization && !a.FixedPosition && a.NextNode != a.DestinationNode))
            {
                //runtime exceeded
                if (Stopwatch.ElapsedMilliseconds / 1000.0 > RuntimeLimitPerAgent * agents.Count * 0.9 || Stopwatch.ElapsedMilliseconds / 1000.0 > RunTimeLimitOverall)
                {
                    Communicator.SignalTimeout();
                    return;
                }

                //remove existing reservations of previous calculations that are not a victim of the reorganization
                _reservationTable.Remove(_calculatedReservations[agent.ID]);

                //all reservations deleted
                _calculatedReservations[agent.ID].Clear();

                if (!UseBias)
                {
                    //Create RRA* search if necessary.
                    //Necessary if the agent has none or the agents destination has changed
                    ReverseResumableAStar rraStar;
                    if (!rraStars.TryGetValue(agent.ID, out rraStar) || rraStar == null || rraStar.StartNode != agent.DestinationNode ||
                        UseDeadlockHandler && _deadlockHandler.IsInDeadlock(agent, currentTime)) // TODO this last expression is used to set back the state of the RRA* in case of a deadlock - this is only a hotfix
                        rraStars[agent.ID] = new ReverseResumableAStar(Graph, agent, agent.Physics, agent.DestinationNode);
                }

                //search my path to the goal
                var aStar = new SpaceTimeAStar(Graph, LengthOfAWaitStep, currentTime + LengthOfAWindow, _reservationTable, agent, rraStars[agent.ID]);
                aStar.FinalReservation = true;

                List<int> nodes = null;
                if (UseBias)
                {
                    aStar.BiasedCost = bias;

                    if (rraStars[agent.ID].Closed.Contains(agent.NextNode) || rraStars[agent.ID].Search(agent.NextNode))
                    {
                        //subtract my own cost
                        nodes = rraStars[agent.ID].getPathAsNodeList(agent.NextNode);
                        nodes.Add(agent.NextNode);
                        foreach (var node in nodes)
                            bias[node] -= LengthOfAWaitStep * 1.0001;
                    }
                }


                //execute
                var found = aStar.Search();

                if (UseBias && nodes != null)
                {
                    //re add my own cost
                    foreach (var node in nodes)
                        bias[node] += LengthOfAWaitStep * 1.0001;
                }

                if (!found)
                {
                    //set a fresh reservation for my current node
                    _reservationTable.Clear(agent.NextNode);
                    _calculatedReservations[agent.ID] = new List<ReservationTable.Interval>(new ReservationTable.Interval[] { new ReservationTable.Interval(agent.NextNode, 0, double.PositiveInfinity) });
                    _reservationTable.Add(_calculatedReservations[agent.ID]);
                    agent.Path = new Path();

                    //clear all reservations of other agents => they will not calculate a path over this node anymore
                    foreach (var otherAgent in _calculatedReservations.Keys.Where(id => id != agent.ID))
                        _calculatedReservations[otherAgent].RemoveAll(r => r.Node == agent.NextNode);

                    //add wait step
                    agent.Path.AddFirst(agent.NextNode, true, LengthOfAWaitStep);

                    //add the next node again
                    if (agent.ReservationsToNextNode.Count > 0 && (agent.Path.Count == 0 || agent.Path.NextAction.Node != agent.NextNode || agent.Path.NextAction.StopAtNode == false))
                        agent.Path.AddFirst(agent.NextNode, true, 0);
                    continue;
                }

                //just wait where you are and until the search time reaches currentTime + LenghtOfAWindow is always a solution
                Debug.Assert(found);

                //+ WHCA* Nodes
                agent.Path = new Path();

                List<ReservationTable.Interval> reservations;
                aStar.GetPathAndReservations(ref agent.Path, out reservations);
                _calculatedReservations[agent.ID] = reservations;

                //add to reservation table
                _reservationTable.Add(_calculatedReservations[agent.ID]);


                //add reservation to infinity
                var lastNode = (_calculatedReservations[agent.ID].Count > 0) ? _calculatedReservations[agent.ID][_calculatedReservations[agent.ID].Count - 1].Node : agent.NextNode;
                var lastTime = (_calculatedReservations[agent.ID].Count > 0) ? _calculatedReservations[agent.ID][_calculatedReservations[agent.ID].Count - 1].End : currentTime;
                _calculatedReservations[agent.ID].Add(new ReservationTable.Interval(lastNode, lastTime, double.PositiveInfinity));
                try
                {
                    _reservationTable.Add(lastNode, lastTime, double.PositiveInfinity);
                }
                catch (DisjointIntervalTree.IntervalIntersectionException)
                {
                    //This could technically fail => especially when they come from a station
                }

                //add the next node again
                if (agent.ReservationsToNextNode.Count > 0 && (agent.Path.Count == 0 || agent.Path.NextAction.Node != agent.NextNode || agent.Path.NextAction.StopAtNode == false))
                    agent.Path.AddFirst(agent.NextNode, true, 0);

                //next time ready?
                if (agent.Path.Count == 0)
                    rraStars[agent.ID] = null;

                //deadlock?
                if (UseDeadlockHandler)
                    if (_deadlockHandler.IsInDeadlock(agent, currentTime))
                        _deadlockHandler.RandomHop(agent);
            }

        }
        /// <summary>
        /// Find single path to the goal within the window and ending time of a bot, using current reservation table.
        /// </summary>
        /// <param name="endTime">Ending time of the bot reach the goal or reach the end of the window.</param>
        /// <param name="currentTime">The time when the path finding start from.</param>
        /// <param name="agent">Agent.Path is the path within window.</param>
        /// <returns>false, if no path can be found.</returns>
        public bool findPath(out double endTime, double currentTime, Agent agent)
        {
            if (agent != null) 
            {
                // store reservation
                var interval = _reservationTable.Get(agent.NextNode, currentTime, currentTime + LengthOfAWindow);
                // remove reservation of the starting point, because WHCAn* will reserve the ending waypoint of the existed path of the bot
                if(interval != null) _reservationTable.Remove(interval);
                
                if (!UseBias)
                {
                    //Create RRA* search if necessary.
                    //Necessary if the agent has none or the agents destination has changed
                    ReverseResumableAStar rraStar;
                    if (!rraStars.TryGetValue(agent.ID, out rraStar) || rraStar == null || rraStar.StartNode != agent.DestinationNode ||
                        UseDeadlockHandler && _deadlockHandler.IsInDeadlock(agent, currentTime)) // TODO this last expression is used to set back the state of the RRA* in case of a deadlock - this is only a hotfix
                        rraStars[agent.ID] = new ReverseResumableAStar(Graph, agent, agent.Physics, agent.DestinationNode);
                }

                //search my path to the goal (within time window)
                var aStar = new SpaceTimeAStar(Graph, LengthOfAWaitStep, currentTime + LengthOfAWindow, _reservationTable, agent, rraStars[agent.ID]);
                aStar.FinalReservation = true;
                //execute
                var found = aStar.Search();
                // Add removed reservation back
                if(interval != null) _reservationTable.Add(interval);

                if(found) 
                {
                    List<ReservationTable.Interval> reservations;
                    aStar.GetPathAndReservations(ref agent.Path, out reservations);
                    endTime = reservations.Last().End;
                    return true;
                }
            }
            endTime = double.MaxValue;
            return false;
        }
    
        /// <summary>
        /// Initialization of scheduling paths based on current reservation table. 
        /// Scheduled paths will not affect real reservation table.
        /// </summary>
        public void scheduleInit()
        {
            // copy reservation table
            scheduledTable = _reservationTable.DeepCopy();
            scheduledPath = new();
            scheduleSequence = new();
        }
        /// <summary>
        /// Find path based on schedule table, will add path to schedule table if success.
        /// </summary>
        /// <param name="overwrite">input true to overwrite previous schedule path of the agent</param>
        /// <returns>false, if can't find path</returns>
        public bool schedulePath(out double endTime, ref List<ReservationTable.Interval> path, double startTime, Agent agent)
        {
            if (agent != null) 
            {
                // init schedule path of the agent
                if(!scheduledPath.ContainsKey(agent.ID))
                {
                    scheduledPath[agent.ID] = new List<ReservationTable.Interval>(_calculatedReservations[agent.ID]);
                    // remove reservation of the starting point, because WHCAn* will reserve the ending waypoint of the existed path of the bot
                    var interval = scheduledTable.Get(agent.NextNode, startTime, startTime + LengthOfAWindow);
                    if(interval != null) scheduledTable.Remove(interval); // since only scheduling, no need to add back
                }
                
                if (!UseBias)
                {
                    //Create RRA* search if necessary.
                    //Necessary if the agent has none or the agents destination has changed
                    ReverseResumableAStar rraStar;
                    if (!rraStars.TryGetValue(agent.ID, out rraStar) || rraStar == null || rraStar.StartNode != agent.DestinationNode ||
                        UseDeadlockHandler && _deadlockHandler.IsInDeadlock(agent, startTime)) // TODO this last expression is used to set back the state of the RRA* in case of a deadlock - this is only a hotfix
                        rraStars[agent.ID] = new ReverseResumableAStar(Graph, agent, agent.Physics, agent.DestinationNode);
                }

                // ignore the bot's path for now
                scheduledTable.CarefulRemoves(scheduledPath[agent.ID]);
                // consider extra path
                scheduledTable.Add(path);

                //search my path to the goal (within time window)
                var aStar = new SpaceTimeAStar(Graph, LengthOfAWaitStep, startTime + LengthOfAWindow, scheduledTable, agent, rraStars[agent.ID]);
                aStar.FinalReservation = true;
                //execute
                var found = aStar.Search();

                // remove extra path
                scheduledTable.CarefulRemoves(path);
                // add ignore bot's path back
                scheduledTable.Add(scheduledPath[agent.ID]);
                
                if(found) 
                {
                    aStar.GetPathAndReservations(ref agent.Path, out List<ReservationTable.Interval> reservations);
                    if(reservations.Count == 0) // happens when no path found in time window
                    {
                        endTime = double.MaxValue;
                        return false;
                    }
                    else
                    {
                        endTime = reservations.Last().End;
                        path.AddRange(reservations);
                        return true;
                    }
                }
            }
            endTime = double.MaxValue;
            return false;
        }
        /// <summary>
        /// Initialization of scheduling paths based on current reservation table. 
        /// Scheduled paths will not affect real reservation table.
        /// </summary>
        public void OverwriteScheduledPath(int ID,  List<ReservationTable.Interval> path)
        {
            // remove previous scheduled path
            scheduledTable.CarefulRemoves(scheduledPath[ID]);
            // add new scheduled path
            scheduledPath[ID] = path;
            scheduledTable.Add(path);
            // store the priority of the bot
            if(scheduleSequence.Contains(ID))
                scheduleSequence.Remove(ID);
            scheduleSequence.Insert(0, ID);
        }
        /// <summary>
        /// Find the starting time of the last reservation of a point if the point
        /// is the ending point of a reserved path.
        /// </summary>
        /// <returns>false, if the input point does not have reservation to infinity.</returns>
        virtual public bool findEndReservation(out double startTime, int node)
        {
            var interval  = _reservationTable.GetLast(node);
            if (interval == null || !double.IsInfinity(interval.End))
            {
                startTime = -1;
                return false;
            }
            else
            { 
                startTime = interval.Start;
                return true;
            }
        }

        /// <summary>
        /// Update the priority of the agent used in FindPaths.
        /// </summary>
        public void UpdateAgentPriority(int ID, int priority)
        {
            AgentPriorities[ID] = priority;
        }
    }
}
