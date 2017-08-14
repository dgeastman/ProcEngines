﻿/*MIT License

Copyright (c) 2017 Michael Ferrara

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.*/
using System;
using System.Collections.Generic;
using UnityEngine;
using ProcEngines.PropellantConfig;
using ProcEngines.EngineGUI;

namespace ProcEngines.EngineConfig
{
    class EngineCalculatorGasGen : EngineCalculatorBase
    {
        double oxPumpPresRiseMPa;
        double fuelPumpPresRiseMPa;
        double oxPumpPowerW;
        double fuelPumpPowerW;

        double turbinePresRatio;
        double turbineInletTempK = 1000;
        double turbineMassFlow;
        double turbinePower;

        static double maxTurbineInletK = 1350;
        static double minTurbineInletK = 700;

        TurbopumpCalculator turbopump;

        EngineDataPrefab gasGenPrefab;
        double massFlowFrac;
        bool oxRich;

        public EngineCalculatorGasGen(BiPropellantConfig mixture, double oFRatio, double chamberPresMPa, double areaRatio, double throatDiameter)
            : base(mixture, oFRatio, chamberPresMPa, areaRatio, throatDiameter) { }

        public override string EngineCalculatorType()
        {
            return "Gas Generator";
        }

        #region EnginePerformanceCalc
        public override void CalculateEngineProperties()
        {
            if (turbopump == null)
                turbopump = new TurbopumpCalculator(biPropConfig, this);

            CalculateMainCombustionChamberParameters();
            AssumePumpPressureRise();
            SolveGasGenTurbine(oxRich);
            CalculateEngineAndNozzlePerformanceProperties();
        }

        void UpdateGasGenProperties(int oxRichInt, double turbineInletTemp)
        {
            bool unchanged = true;

            if(oxRichInt <= 0 && oxRich)
            {
                oxRich = false;
                unchanged = false;
            }
            else if (oxRichInt > 0 && !oxRich)
            {
                oxRich = true;
                unchanged = false;
            }

            if (turbineInletTemp < minTurbineInletK)
                turbineInletTemp = minTurbineInletK;
            if (turbineInletTemp > maxTurbineInletK)
                turbineInletTemp = maxTurbineInletK;

            unchanged &= turbineInletTemp == turbineInletTempK;
            turbineInletTempK = turbineInletTemp;

            if (!unchanged)
                CalculateEngineProperties();
        }
        #endregion

        #region TurbopumpCalc
        void AssumePumpPressureRise()
        {
            oxPumpPresRiseMPa = chamberPresMPa * (1.0 + injectorPressureRatioDrop) - tankPresMPa;
            fuelPumpPresRiseMPa = chamberPresMPa * (1.0 + injectorPressureRatioDrop) * (1.0 + regenerativeCoolingPresDrop) - tankPresMPa;      //assume that only fuel is used for regen cooling
        }

        void SolveGasGenTurbine(bool oxRich)
        {
            double gasGenInjectorPresDrop = injectorPressureRatioDrop * 3.0 / 2.0;
            if (gasGenInjectorPresDrop < 0.2)
                gasGenInjectorPresDrop = 0.2;

            double gasGenPresMPa = chamberPresMPa * (1.0 + injectorPressureRatioDrop) / (1.0 + gasGenInjectorPresDrop);

            turbinePresRatio = gasGenPresMPa / (0.3);       //assume ~3 atm ~= 0.3MPa backpressure

            gasGenPrefab = biPropConfig.CalcDataAtPresAndTemp(gasGenPresMPa, turbineInletTempK, oxRich);        //assume that gas gen runs at same pressure as chamber

            double gammaPower = -(gasGenPrefab.nozzleGamma - 1.0) / gasGenPrefab.nozzleGamma;
            double outputTempMin = 400;

            turbinePresRatio = Math.Min(turbinePresRatio, Math.Pow(turbineInletTempK / outputTempMin, -1/gammaPower));    //add stop for ensuring that not too much power is extracted

            turbinePresRatio = Math.Min(turbinePresRatio, 16);      //add upper limit for pres ratio

            /*double gasGenOFRatio = gasGenPrefab.OFRatio;
            double gammaPower = gasGenPrefab.nozzleGamma / (gasGenPrefab.nozzleGamma - 1.0);
            double Cp = gasGenPrefab.CalculateCp();*/

            double[] gasGenOFRatio_gammaPower_Cp_Dens = new double[] { gasGenPrefab.OFRatio,
                gammaPower,
                gasGenPrefab.chamberCp,
                biPropConfig.GetOxDensity(),
                biPropConfig.GetFuelDensity()};

            turbineMassFlow = MathUtils.BrentsMethod(IterateSolveGasGenTurbine, gasGenOFRatio_gammaPower_Cp_Dens, 0, 1.5 * massFlowChamber, 0.000001, int.MaxValue);

            massFlowTotal += turbineMassFlow;

            overallOFRatio = chamberOFRatio;
            overallOFRatio *= massFlowChamber / massFlowTotal;
            overallOFRatio += gasGenPrefab.OFRatio * turbineMassFlow / massFlowTotal;

            massFlowFrac = turbineMassFlow / massFlowTotal;
        }

        double IterateSolveGasGenTurbine(double turbineMassFlow, double[] gasGenOFRatio_gammaPower_Cp_Dens)
        {
            double turbineEfficiency = 0.6;             //TODO: make vary with tech level

            double turbineMassFlowFuel = turbineMassFlow / (gasGenOFRatio_gammaPower_Cp_Dens[0] + 1.0);
            double turbineMassFlowOx = turbineMassFlowFuel * gasGenOFRatio_gammaPower_Cp_Dens[0];

            double massFlowFuelTotal = turbineMassFlowFuel + massFlowChamberFuel;
            double massFlowOxTotal = turbineMassFlowOx + massFlowChamberOx;

            turbopump.CalculatePumpProperties(massFlowOxTotal, massFlowFuelTotal, tankPresMPa, oxPumpPresRiseMPa, fuelPumpPresRiseMPa);

            double requiredPower = turbopump.RequiredPower();

            turbinePower = requiredPower / (turbineEfficiency);

            double checkTurbineMassFlow = (1.0 - Math.Pow(turbinePresRatio, gasGenOFRatio_gammaPower_Cp_Dens[1]));
            checkTurbineMassFlow *= gasGenOFRatio_gammaPower_Cp_Dens[2] * turbineInletTempK;
            checkTurbineMassFlow = turbinePower / (1000.0 * checkTurbineMassFlow);   //convert to tonnes

            double massFlowDiff = (checkTurbineMassFlow - turbineMassFlow);
            return massFlowDiff;
        }
        #endregion

        #region GUI
        bool showGasGen = false;
        bool showExhaust = false;

        int oxRichInt = 0;
        static string[] fuelOxRichString = new string[] { "Fuel Rich", "Ox Rich" };

        protected override void LeftSideEngineGUI()
        {
            if (GUILayout.Button("Gas Generator Design"))
                showGasGen = !showGasGen;
            if (showGasGen)
            {
                double tmpTurbineInletTempK = turbineInletTempK;
                tmpTurbineInletTempK = GUIUtils.TextEntryForDoubleWithButtons("Temperature, K:", 125, tmpTurbineInletTempK, 10, 100, 50, "F0");

                //Gas Gen OxRich, FuelRich
                GUILayout.BeginHorizontal();
                oxRichInt = GUILayout.SelectionGrid(oxRichInt, fuelOxRichString, 2);
                GUILayout.EndHorizontal();

                //Gas Gen Pres
                GUILayout.BeginHorizontal();
                GUILayout.Label("GG Pres MPa: ", GUILayout.Width(125));
                GUILayout.Label(gasGenPrefab.chamberPresMPa.ToString("F3"));
                GUILayout.EndHorizontal();
                //Gas Gen O/F
                GUILayout.BeginHorizontal();
                GUILayout.Label("GG O/F: ", GUILayout.Width(125));
                GUILayout.Label(gasGenPrefab.OFRatio.ToString("F3"));
                GUILayout.EndHorizontal();
                //Gas Gen Mass Flow %
                GUILayout.BeginHorizontal();
                GUILayout.Label("GG % Mass Flow: ", GUILayout.Width(125));
                GUILayout.Label((massFlowFrac * 100.0).ToString("F1") + " %");
                GUILayout.EndHorizontal();

                UpdateGasGenProperties(oxRichInt, tmpTurbineInletTempK);
            }
        }

        protected override void RightSideEngineGUI()
        {
            turbopump.TurbopumpGUI();
            if (GUILayout.Button("Turbine Exhaust Design"))
                showExhaust = !showExhaust;
            if (showExhaust)
            {
                //Turbopump Pump Setup
                GUILayout.Label("Select straight, exhaust to nozzle, or vernier");
            }
        }
        #endregion
    }
}
