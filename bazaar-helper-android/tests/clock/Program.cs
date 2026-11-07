using BazaarHelper;
var clock = new CombatFrameClock();int checks=0;
void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
Check(!clock.HasCombat,"hidden initially");clock.Begin(4);Check(clock.Active && clock.ElapsedMilliseconds==0,"begin zero");
clock.Advance();Check(clock.ElapsedMilliseconds==0,"first frame is frame zero");clock.Advance();Check(clock.ElapsedMilliseconds==50,"50ms frame");
// Without processed frames, pause/slow motion cannot advance the timer.
Check(clock.ElapsedMilliseconds==50,"no wall clock advance");clock.Advance();clock.Advance();clock.Advance();Check(clock.ElapsedMilliseconds==150,"cap at final frame");
clock.End();clock.Advance();Check(!clock.Active&&clock.HasCombat&&clock.ElapsedMilliseconds==150,"freeze completed time");clock.Begin(2001);for(int i=0;i<2001;i++)clock.Advance();Check(clock.ElapsedMilliseconds==100000,"long fight logical duration");
clock.Reset();Check(!clock.HasCombat&&!clock.Active&&clock.ElapsedMilliseconds==0,"leave run hides and clears");clock.Begin(-3);clock.Advance();Check(clock.ElapsedMilliseconds==0,"empty input bounded");clock.Begin(2);clock.Advance();clock.Advance();Check(clock.ElapsedMilliseconds==50,"next fight restarts");Console.WriteLine($"PASS {checks} combat clock checks");
