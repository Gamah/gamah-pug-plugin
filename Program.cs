using System;using Gpp;
int budget = HudMetrics.Width(WorldLayout.Rule(WorldLayout.MainBoardWidth));
string[] lines = {
 ".captain  AGAIN TO TAKE IT BACK", ".captain  TO BE A CAPTAIN",
 "TWO ARE DRAWN FROM WHOEVER DOES", "EVERYBODY ELSE GETS DRAFTED",
 "NOBODY IN = TWO AT RANDOM", ".captain AGAIN TO TAKE IT BACK",
 ".captain TO CAPTAIN - TWO ARE DRAWN", "CAPTAINS WILL BE DRAWN AT RANDOM"};
Console.WriteLine($"budget {budget}");
foreach (var l in lines) Console.WriteLine($"{HudMetrics.Width(l),6} {(HudMetrics.Width(l)<=budget?"ok  ":"OVER")} {l}");
