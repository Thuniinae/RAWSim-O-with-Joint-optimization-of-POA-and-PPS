﻿using System;
using System.Collections.Generic;
using System.Linq;
using RAWSimO.Core.Configurations;
using RAWSimO.Core.Elements;
using RAWSimO.Core.Interfaces;
using RAWSimO.Core.Items;
using RAWSimO.Toolbox;

namespace RAWSimO.Core.Control
{
    #region OrderManager

    /// <summary>
    /// Implements the core order manager functionality.
    /// </summary>
    public abstract class OrderManager : IUpdateable, IOptimize, IStatTracker
    {
        #region Constructor

        /// <summary>
        /// Creates a new order manager.
        /// </summary>
        /// <param name="instance">The instance this order manager belongs to.</param>
        protected OrderManager(Instance instance)
        {
            Instance = instance;

            // Subscribe to events
            Instance.BundleStored += SignalBundleStored;
            Instance.NewOrder += SignalNewOrderAvailable;
            Instance.OrderCompleted += SignalOrderFinished;
        }

        #endregion Constructor

        #region Fields
        /// <summary>
        /// The instance this manager is assigned to.
        /// </summary>
        protected Instance Instance { get; set; }

        /// <summary>
        /// All not yet decided orders.
        /// </summary>
        protected HashSet<Order> _pendingOrders = new HashSet<Order>();
        /// <summary>
        /// public read only all not yet decided orders.
        /// </summary>
        public HashSet<Order> pendingOrders {get => _pendingOrders;}
        /// <summary>
        /// All not yet decided late orders in ascending order of arrival time
        /// </summary>
        public HashSet<Order> pendingLateOrders = new HashSet<Order>();
        /// <summary>
        /// All not yet decided not late orders in ascending order of arrival time
        /// </summary>
        public HashSet<Order> pendingNotLateOrders = new HashSet<Order>();
        /// <summary>
        /// indicate total available capacity is less than late orders
        /// </summary>
        public bool lateOrdersEnough;
        /// <summary>
        ///已分配给工作站的Pods
        /// </summary>
        protected Dictionary<OutputStation, HashSet<Pod>> _inboundPodsPerStation = new Dictionary<OutputStation, HashSet<Pod>>();
        

        /// <summary>
        /// Indicates that the current situation has already been investigated. So that it will be ignored.
        /// </summary>
        protected bool SituationInvestigated { get; set; }
        /// <summary>
        /// 工作站的当前可用容量
        /// </summary>
        public Dictionary<OutputStation, int> Cs = new Dictionary<OutputStation, int>();
        /// <summary>
        /// 产生Cs
        /// </summary>
        /// <returns></returns>
        public void GenerateCs()
        {
            foreach (var station in Instance.OutputStations)
            {
                if (Cs.ContainsKey(station))
                    Cs[station] = station.Capacity - station.CapacityReserved - station.CapacityInUse;
                else
                    Cs.Add(station, station.Capacity - station.CapacityReserved - station.CapacityInUse);
            }
        }


        #endregion Fields

        #region Methods (implemented)

        /// <summary>
        /// Immediately submits the order to the station.
        /// </summary>
        /// <param name="order">The order that is going to be allocated.</param>
        /// <param name="station">The station the order is assigned to.</param>
        public void AllocateOrder(Order order, OutputStation station)
        {
            // Update lists
            _pendingOrders.Remove(order);
            // Update intermediate capacity information
            station.RegisterOrder(order);
            // Submit the decision
            Instance.Controller.Allocator.Allocate(order, station);
        }

        /// <summary>
        /// Gets all pending orders that are fulfillable by their actually available stock.
        /// </summary>
        /// <returns>An array of pending orders.</returns>
        protected HashSet<Order> GetPendingAvailableStockOrders() =>
            GetPendingAvailableStockOrders(int.MaxValue);

        /// <summary>
        /// Gets all pending orders that are fulfillable by their actually available stock.
        /// </summary>
        /// <param name="maxValue">The max amount of orders to take</param>
        /// <returns>A hash-set of pending orders.</returns>
        protected HashSet<Order> GetPendingAvailableStockOrders(int maxValue) =>
            new HashSet<Order>(_pendingOrders.Where(o => o.Positions.All(p => Instance.StockInfo.GetActualStock(p.Key) >= p.Value)).Take(maxValue));

        #endregion Methods (implemented)

        #region Signals

        /// <summary>
        /// Signals the manager that the order was submitted to the system.
        /// </summary>
        /// <param name="order">The order that was allocated.</param>
        /// <param name="station">The station this order was allocated to.</param>
        public void SignalOrderAllocated(Order order, OutputStation station) { /* Not in use anymore */ }

        /// <summary>
        /// Signals the manager that the given order was completed.
        /// </summary>
        /// <param name="order">The order that was completed.</param>
        /// <param name="station">The station at which the order was completed.</param>
        public void SignalOrderFinished(Order order, OutputStation station) { SituationInvestigated = false; }

        /// <summary>
        /// Signals the manager that the order was submitted to the system.
        /// </summary>
        /// <param name="order">The order that was allocated.</param>
        public void SignalNewOrderAvailable(Order order) { SituationInvestigated = false; }

        /// <summary>
        /// Signals the manager that the bundle was placed on the pod.
        /// </summary>
        /// <param name="bundle">The bundle that was stored.</param>
        /// <param name="station">The station the bundle was assigned to.</param>
        /// <param name="pod">The pod the bundle is stored in.</param>
        /// <param name="bot">The bot that fetched the bundle.</param>
        public void SignalBundleStored(InputStation station, Bot bot, Pod pod, ItemBundle bundle) { SituationInvestigated = false; }

        /// <summary>
        /// Signals the manager that a station that was previously not in use can now be assigned orders.
        /// </summary>
        /// <param name="station">The newly activated station.</param>
        public void SignalStationActivated(OutputStation station) { SituationInvestigated = false; }

        /// <summary>
        /// Signals Fully-Supplied order manager that new pod is assigned to station,  so new orders can be assigned
        /// </summary>
        public void SignalPodAssigned() { SituationInvestigated = false; }

        #endregion Signals

        #region Methods (abstract)

        /// <summary>
        /// This is called to decide about potentially pending orders.
        /// This method is being timed for statistical purposes and is also ONLY called when <code>SituationInvestigated</code> is <code>false</code>.
        /// Hence, set the field accordingly to react on events not tracked by this outer skeleton.
        /// </summary>
        protected abstract void DecideAboutPendingOrders();

        #endregion Methods (abstract)

        #region IUpdateable Members

        /// <summary>
        /// The next event when this element has to be updated.
        /// </summary>
        /// <param name="currentTime">The current time of the simulation.</param>
        /// <returns>The next time this element has to be updated.</returns>
        public virtual double GetNextEventTime(double currentTime) { return double.PositiveInfinity; }
        /// <summary>
        /// Updates the element to the specified time.
        /// </summary>
        /// <param name="lastTime">The time before the update.</param>
        /// <param name="currentTime">The time to update to.</param>
        public virtual void Update(double lastTime, double currentTime)
        {
            // Retrieve the next order that we have not seen so far
            var order = Instance.ItemManager.RetrieveOrder(this);
            while (order != null)
            {
                order.TimePlaced = Instance.SettingConfig.StartTime.AddSeconds(Convert.ToInt32(Instance.Controller.CurrentTime));
                // Add the bundle
                _pendingOrders.Add(order);
                // Retrieve the next bundle that we have not seen so far
                order = Instance.ItemManager.RetrieveOrder(this);
                // Mark new situation
                SituationInvestigated = false;
            }

            // assess late orders
            pendingLateOrders = new HashSet<Order>(_pendingOrders.Where(o => o.DueTime < this.Instance.Controller.CurrentTime).OrderBy(o => o.TimeStamp));
            pendingNotLateOrders = _pendingOrders.ExceptWithNew(pendingLateOrders);

            // assess for Fully-Supplied
            if (this.Instance.OutputStations.Sum(s => s.Capacity - s.CapacityInUse - s.CapacityReserved) < pendingLateOrders.Count)
                lateOrdersEnough = true;
            else
                lateOrdersEnough = false;

            // Decide about remaining orders
            if (!SituationInvestigated)
            {
                // Measure time for decision
                DateTime before = DateTime.Now;
                // Do the actual work
                DecideAboutPendingOrders();
                // Calculate decision time
                Instance.Observer.TimeOrderBatching((DateTime.Now - before).TotalSeconds);
                // Remember that we had a look at the situation
                SituationInvestigated = true;
            }
        }

        #endregion

        #region IOptimize Members

        /// <summary>
        /// Signals the current time to the mechanism. The mechanism can decide to block the simulation thread in order consume remaining real-time.
        /// </summary>
        /// <param name="currentTime">The current simulation time.</param>
        public abstract void SignalCurrentTime(double currentTime);

        #endregion

        #region IStatTracker Members

        /// <summary>
        /// The callback that indicates that the simulation is finished and statistics have to submitted to the instance.
        /// </summary>
        public virtual void StatFinish() { /* Default case: do not flush any statistics */ }

        /// <summary>
        /// The callback indicates a reset of the statistics.
        /// </summary>
        public virtual void StatReset() { /* Default case: nothing to reset */ }

        #endregion
    }

    #endregion OrderManager
}
