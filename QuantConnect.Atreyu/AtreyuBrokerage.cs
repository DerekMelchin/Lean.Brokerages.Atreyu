﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using QuantConnect.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Atreyu.Client;
using QuantConnect.Atreyu.Client.Messages;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;
using HistoryRequest = QuantConnect.Data.HistoryRequest;
using Order = QuantConnect.Orders.Order;

namespace QuantConnect.Atreyu
{
    public partial class AtreyuBrokerage : Brokerage
    {
        private readonly IAlgorithm _algorithm;
        private readonly IOrderProvider _orderProvider;
        private readonly ZeroMQConnectionManager _zeroMQ;
        private readonly ISymbolMapper _symbolMapper;
        private readonly ISecurityProvider _securityProvider;

        /// <summary>
        /// Checks if the ZeroMQ is connected
        /// </summary>
        public override bool IsConnected => _zeroMQ.IsConnected;

        /// <summary>
        /// Creates a new <see cref="AtreyuBrokerage"/> from the specified values retrieving data from configuration file
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        public AtreyuBrokerage(IAlgorithm algorithm)
            : this(Config.Get("atreyu-host"),
                Config.GetInt("atreyu-req-port"),
                Config.GetInt("atreyu-sub-port"),
                Config.Get("atreyu-username"),
                Config.Get("atreyu-password"),
                algorithm)
        { }

        /// <summary>
        ///  Creates a new <see cref="AtreyuBrokerage"/> from the specified values
        /// </summary>
        /// <param name="host">Instance url</param>
        /// <param name="requestPort">Port for request/reply (REQREP) messaging pattern</param>
        /// <param name="subscribePort">Port for publish/subscribe (PUBSUB) messaging pattern</param>
        /// <param name="username">The login user name</param>
        /// <param name="password">The login password</param>
        /// <param name="algorithm">The algorithm instance</param>
        public AtreyuBrokerage(
            string host,
            int requestPort,
            int subscribePort,
            string username,
            string password,
            IAlgorithm algorithm) : base("Atreyu")
        {
            if (algorithm == null)
            {
                throw new ArgumentNullException(nameof(algorithm));
            }

            _algorithm = algorithm;
            _orderProvider = _algorithm.Transactions;
            _securityProvider = _algorithm.Portfolio;
            _symbolMapper = new AtreyuSymbolMapper();

            _zeroMQ = new ZeroMQConnectionManager(host, requestPort, subscribePort, username, password);
            _zeroMQ.MessageRecieved += (s, e) => OnMessage(e);
        }

        public override List<Order> GetOpenOrders()
        {
            if (Log.DebuggingEnabled)
            {
                Log.Debug("AtreyuBrokerage.GetOpenOrders()");
            }
            var response = _zeroMQ.Send<OpenOrdersResultMessage>(new QueryOpenOrdersMessage());

            if (response == null)
            {
                throw new Exception("AtreyuBrokerage.GetOpenOrders: message was not sent.");
            }

            if (response.Status != 0)
            {
                throw new Exception($"AtreyuBrokerage.GetOpenOrders: request failed: [{(int)response.Status}] ErrorMessage: {response.Text}");
            }

            if (response.Orders?.Any() != true)
            {
                return new List<Order>();
            }

            var result = response.Orders
                .Select(ConvertOrder)
                .ToList();

            return result;
        }

        public override List<Holding> GetAccountHoldings()
        {
            if (Log.DebuggingEnabled)
            {
                Log.Debug("AtreyuBrokerage.GetAccountHoldings()");
            }
            var response = _zeroMQ.Send<OpenPositionsResultMessage>(new QueryPositionsMessage());

            if (response == null)
            {
                throw new Exception("AtreyuBrokerage.GetAccountHoldings: message was not sent.");
            }

            if (response.Status != 0)
            {
                throw new Exception($"AtreyuBrokerage.GetAccountHoldings: request failed: [{(int)response.Status}] ErrorMessage: {response.Text}");
            }

            if (response.Positions?.Any() != true)
            {
                return new List<Holding>();
            }

            return response.Positions
                .Select(ConvertHolding)
                .ToList();
        }

        public override List<CashAmount> GetCashBalance()
        {
            if (Log.DebuggingEnabled)
            {
                Log.Debug("AtreyuBrokerage.GetCashBalance()");
            }
            return new List<CashAmount>() { new CashAmount(1000, "USD") };
            //throw new NotImplementedException();
        }

        public override bool PlaceOrder(Order order)
        {
            if (order.AbsoluteQuantity % 1 != 0)
            {
                throw new ArgumentException(
                    $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: Quantity has to be an Integer, but sent {order.Quantity}");
            }

            var request = new NewEquityOrderMessage()
            {
                Side = ConvertDirection(order.Direction),
                Symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                ClOrdID = Guid.NewGuid().ToString().Replace("-", string.Empty),
                OrderQty = (int)order.AbsoluteQuantity,
                TimeInForce = ConvertTimeInForce(order.TimeInForce),
                // DeliverToCompID = "CS", // exclude for testing purposes
                ExDestination = "NSDQ",
                ExecInst = "1",
                HandlInst = "1",
                TransactTime = DateTime.UtcNow.ToString(DateFormat.FIXWithMillisecond, CultureInfo.InvariantCulture)
            };

            switch (order.Type)
            {
                case OrderType.Market:
                    request.OrdType = "1";
                    break;
                case OrderType.Limit:
                    request.OrdType = "2";
                    request.Price = (order as LimitOrder)?.LimitPrice ?? order.Price;
                    break;
                case OrderType.MarketOnClose:
                    request.OrdType = "5";
                    break;
                default:
                    throw new NotSupportedException($"AtreyuBrokerage.ConvertOrderType: Unsupported order type: {order.Type}");
            }

            if (order.Type == OrderType.Limit && (order.Properties is AtreyuOrderProperties orderProperties))
            {
                if (orderProperties.PostOnly)
                {
                    request.RoutingPolicy = "P";
                }
            }

            var submitted = false;
            WithLockedStream(() =>
            {
                var response = _zeroMQ.Send<SubmitResponseMessage>(request);

                if (response == null)
                {
                    throw new Exception("AtreyuBrokerage.PlaceOrder: message was not sent.");
                }

                if (response.Status == 0)
                {
                    order.BrokerId.Add(response.ClOrdID);
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.TransactTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.Submitted
                    });
                    Log.Trace($"Order submitted successfully - OrderId: {order.Id}");
                    submitted = true;
                }
                else
                {
                    var message =
                        $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {response.Text}";
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.SendingTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.Invalid
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                }
            });
            return submitted;
        }

        public override bool UpdateOrder(Order order)
        {
            if (order.BrokerId.Count == 0)
            {
                throw new ArgumentNullException(nameof(order.BrokerId), "AtreyuBrokerage.UpdateOrder: There is no brokerage id to be updated for this order.");
            }

            if (order.BrokerId.Count > 1)
            {
                throw new NotSupportedException("AtreyuBrokerage.UpdateOrder: Multiple orders update not supported. Please cancel and re-create.");
            }

            if (order.AbsoluteQuantity % 1 != 0)
            {
                throw new ArgumentException(
                    $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: Quantity has to be an Integer, but sent {order.Quantity}");
            }

            var request = new CancelReplaceEquityOrderMessage()
            {
                ClOrdID = order.BrokerId.First(),
                OrderQty = (int)order.AbsoluteQuantity,
                OrigClOrdID = order.BrokerId.First(),
                TransactTime = DateTime.UtcNow.ToString(DateFormat.FIXWithMillisecond, CultureInfo.InvariantCulture)
            };

            if (order.Type == OrderType.Limit)
            {
                request.Price = (order as LimitOrder)?.LimitPrice ?? order.Price;
            }

            var submitted = false;
            WithLockedStream(() =>
            {
                var response = _zeroMQ.Send<SubmitResponseMessage>(request);

                if (response == null)
                {
                    throw new Exception("AtreyuBrokerage.UpdateOrder: message was not sent.");
                }

                if (response.Status == 0)
                {
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.TransactTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.UpdateSubmitted
                    });
                    Log.Trace($"Replace submitted successfully - OrderId: {order.Id}");

                    submitted = true;
                }
                else
                {
                    var message = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {response.Text}";
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.SendingTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.Invalid
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                }
            });

            return submitted;
        }

        public override bool CancelOrder(Order order)
        {
            if (!order.BrokerId.Any())
            {
                // we need the brokerage order id in order to perform a cancellation
                Log.Trace("AtreyuBrokerage.CancelOrder(): Unable to cancel order without BrokerId.");
                return false;
            }

            var submitted = false;
            WithLockedStream(() =>
            {
                var response = _zeroMQ.Send<SubmitResponseMessage>(new CancelEquityOrderMessage()
                {
                    ClOrdID = order.BrokerId.First(),
                    OrigClOrdID = order.BrokerId.First(),
                    TransactTime = DateTime.UtcNow.ToString(DateFormat.FIXWithMillisecond, CultureInfo.InvariantCulture)
                });

                if (response == null)
                {
                    throw new Exception("AtreyuBrokerage.CancelOrder: message was not sent.");
                }

                if (response.Status == 0)
                {
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.TransactTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.CancelPending
                    });
                    Log.Trace($"Cancel submitted successfully - OrderId: {order.Id}");
                    submitted = true;
                }
                else
                {
                    var message =
                        $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {response.Text}";
                    OnOrderEvent(new OrderEvent(
                        order,
                        Time.ParseFIXUtcTimestamp(response.SendingTime),
                        OrderFee.Zero,
                        "Atreyu Order Event")
                    {
                        Status = OrderStatus.Invalid
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                }
            });
            return submitted;
        }

        public override void Connect()
        {
            if (!_zeroMQ.IsConnected)
                _zeroMQ.Connect();
        }

        public override void Disconnect()
        {
            _zeroMQ.Disconnect();
            _zeroMQ.DisposeSafely();
        }

        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            throw new InvalidOperationException("Atreyu doesn't support history");
        }
    }
}
