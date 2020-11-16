﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QLearningOnPerishableInventory
{
    public class FreezingInventory
    {
        const int ProductLife = 30;
        const int InventoryPositionCount_S1 = 41;
        const int RemainingLivesCount_S1 = InventoryPositionCount_S1 * ProductLife;
        const int OrderQuantitiesCount1 = 9;
        const int a = 4;
        const int b = 5;
        
        const double InitialSalePricePerUnit = 5.0;
        const double PriceDeclinePerDay = (InitialSalePricePerUnit - 2.0) / 30;

        // costs
        const double FreezerCostFixedPerDay = -InitialSalePricePerUnit;
        const double HoldingCostPerDay = -0.2;
        const int LotSize = 5;
        const double OutageCost = -InitialSalePricePerUnit, ShortageCost = -0.5f;
        const double OrderingCost = 3.0;

        const double LearningRate = 0.001;
        const int Episodes = 30_000;
        const int MaxStepPerEpisode = InventoryPositionCount_S1;
        static readonly double EpsilonDecay = Math.Exp(Math.Log(0.1) / Episodes);
        const double FutureDiscount = 0.99;

        static Random randm = new Random(DateTime.UtcNow.Millisecond);

        public static double[] Start(IProgress<double> pr)
        {
            double[] EpisodeRewards = new double[Episodes];

            var OrderQuantities = new int[OrderQuantitiesCount1];
            var InventoryPositions = new int[InventoryPositionCount_S1];
            var RemainingLives = new int[RemainingLivesCount_S1];

            Task.WhenAll(
                Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < OrderQuantitiesCount1; i++)
                    {
                        OrderQuantities[i] = i;
                    }
                }),
                Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < InventoryPositionCount_S1; i++)
                    {
                        InventoryPositions[i] = i;
                    }
                }),
                Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < RemainingLivesCount_S1; i++)
                    {
                        RemainingLives[i] = i;
                    }
                })
            ).Wait();

            var Table1 = new QTable1(InventoryPositions, RemainingLives, OrderQuantities, ProductLife);

            double Epsilon = 1;
            int ep = 0;
            var itr = MathFunctions.GetDemandByGammaDist(a, b, new Random(DateTime.UtcNow.Millisecond)).GetEnumerator();

            while (ep < Episodes)
            {
                double ep_reward = 0;
                double HoldingCost = 0;
                var ProductsOnHand = new List<Product>(InventoryPositionCount_S1);

                for (int step = 0; step < MaxStepPerEpisode; step++)
                {
                    itr.MoveNext();
                    var actual_demand = itr.Current;
                    double PrevHoldingCost = 0;

                    int life_rem = ProductsOnHand.Sum(p => ProductLife - p.LifeSpent);

                    // determine order quantity
                    int oq, lot;
                    int total_product_count = ProductsOnHand.Count;
                    int max_oq = MakeLot(InventoryPositionCount_S1 - total_product_count);
                    var dec_rnd = randm.NextDouble();

                    if (dec_rnd < Epsilon) // explore
                    {
                        lot = OrderQuantities[randm.Next(max_oq)];
                    }
                    else // exploit
                    {
                        var key = Table1.GetMaxOrderQuantityForState(total_product_count, life_rem, max_oq);
                        lot = key.Action;
                    }
                    oq = lot * LotSize;

                    // recieve the arrived products that was ordered previously
                    for (int i = oq - 1; i >= 0; i--)
                    {
                        ProductsOnHand.Add(new Product(0));
                    }

                    // calculate shortage
                    int Ts = Math.Max(0, actual_demand - ProductsOnHand.Count);

                    double sale_price_for_step = 0.0;
                    // remove the products consumed by customers
                    if (ProductsOnHand.Count > 0)
                    {
                        if (actual_demand >= ProductsOnHand.Count)
                        {
                            sale_price_for_step = ProductsOnHand.Sum(p => InitialSalePricePerUnit - p.LifeSpent * PriceDeclinePerDay);
                            ProductsOnHand.Clear();
                        }
                        else
                        {
                            // removing the oldest products
                            sale_price_for_step = ProductsOnHand.Take(actual_demand).Sum(p => InitialSalePricePerUnit - p.LifeSpent * PriceDeclinePerDay);
                            ProductsOnHand.RemoveRange(0, actual_demand);
                        }
                    }

                    // discard outdated product and calculate outage amount
                    int To = 0;
                    var new_life_rem = 0;
                    for (int i = ProductsOnHand.Count - 1; i >= 0; i--)
                    {
                        ProductsOnHand[i].LifeSpent++;
                        if (ProductsOnHand[i].LifeSpent > ProductLife)
                        {
                            To++;
                            ProductsOnHand.RemoveAt(i);
                        }
                        else
                        {
                            PrevHoldingCost += HoldingCostPerDay;
                            new_life_rem += ProductLife - ProductsOnHand[i].LifeSpent;
                        }
                    }

                    double reward = Ts * ShortageCost + 
                                    To * OutageCost + 
                                    HoldingCost +
                                    sale_price_for_step + 
                                    FreezerCostFixedPerDay;

                    HoldingCost = PrevHoldingCost;

                    ep_reward += reward;

                    // calculate max q(s',a')
                    // quantity in next round = on hand inventory + orders in transit - next day demand
                    var new_total_product_count = ProductsOnHand.Count;
                    var max_oq_tmp = MakeLot(OrderQuantities.Last() * 5 - new_total_product_count);
                    var next_maxq_key = Table1.GetMaxOrderQuantityForState(new_total_product_count, new_life_rem, max_oq_tmp);
                    var max_q_future = Table1[next_maxq_key];

                    // update q table
                    var state = new QuantityLifeState(total_product_count, life_rem);
                    var sa_pair = new QTableKey1(state, lot);
                    Table1[sa_pair] = (1 - LearningRate) * Table1[sa_pair] + LearningRate * (reward + FutureDiscount * max_q_future);
                }

                EpisodeRewards[ep] = ep_reward;
                Epsilon *= EpsilonDecay;
                //LearningRate *= EpsilonDecay;
                if (ep % 50 == 0)
                    pr.Report(ep * 100.0 / Episodes);
                ep++;
            }

            return EpisodeRewards;
        }

        static int MakeLot(int product_count, int lot_size = LotSize)
        {
            product_count -= product_count % lot_size;
            product_count /= lot_size;
            return product_count;
        }
    }
}