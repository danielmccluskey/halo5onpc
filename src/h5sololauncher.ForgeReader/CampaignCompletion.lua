-- Adapter around the original shared campaign functions. Mission scripts keep
-- ownership of their objectives, ending cinematics, flags and next-scenario data.
if type(_G['EndMission']) ~= 'function' or type(_G['sys_LoadScenario']) ~= 'function' then
 return false;
end
local previous = rawget(_G, 'H5CampaignCompletion');
if previous and EndMission == previous.EndWrapper then return previous.Drain(); end
local originalEnd, originalStart = EndMission, StartMission;
local originalLoad, originalCinema = sys_LoadScenario, CinematicPlay;
local originalAchievement = CampaignScriptedAchievementUnlocked;
local state = { phase = 'idle', synthetic = false, reports = {} };
rawset(_G, 'H5CampaignCompletion', state);
-- Keep only ordinary Lua values in checkpoint state. A custom C callback (even
-- captured only as an upvalue) cannot be serialized by the native save system.
local function emit(key, value)
 if #state.reports >= 64 then state.reportOverflow = true; return; end
 state.reports[#state.reports + 1] = key .. '|' .. string.gsub(tostring(value or ''), '[' .. string.char(13, 10) .. ']', ' ');
end
state.Drain = function()
 local result = state.reportOverflow and 'failure|Campaign report queue overflow' or table.concat(state.reports, string.char(10));
 state.reports = {}; state.reportOverflow = false;
 return result;
end;
local function timeText(seconds)
 seconds = math.max(0, math.floor(tonumber(seconds) or 0));
 return string.format('%d:%02d', math.floor(seconds / 60), seconds % 60);
end
local function difficultyText()
 local value = game_difficulty_get_real();
 if value == DIFFICULTY.easy then return 'Easy'; end
 if value == DIFFICULTY.normal then return 'Normal'; end
 if value == DIFFICULTY.heroic then return 'Heroic'; end
 if value == DIFFICULTY.legendary then return 'Legendary'; end
 return 'Unknown';
end
local function begin(mission)
 state.phase = 'ending'; state.mission = mission;
 emit('begin', mission and mission.name or 'Campaign mission');
 emit('mode', campaign_metagame_enabled() and 'Score Attack' or 'Campaign');
 emit('synthetic', state.synthetic and 'Diagnostic ending test' or '');
end
state.EndWrapper = function(mission, killing)
 if killing then
  state.phase = 'idle'; state.synthetic = false; emit('reset', 'mission killed');
  return originalEnd(mission, killing);
 end
 if state.phase ~= 'idle' then emit('duplicate', 'EndMission'); return; end
 if mission == nil or mission ~= g_currentMission then return originalEnd(mission, killing); end
 begin(mission);
 originalEnd(mission, killing);
 emit('end_return', state.phase);
end;
EndMission = state.EndWrapper;
StartMission = function(mission, blinking)
 state.phase = 'idle'; state.synthetic = false; state.mission = nil;
 emit('reset', 'StartMission');
 return originalStart(mission, blinking);
end;
CinematicPlay = function(name, ...)
 if state.phase == 'ending' then emit('cinematic_begin', name); end
 local result = originalCinema(name, ...);
 if state.phase == 'ending' then emit('cinematic_end', name); end
 return result;
end;
CampaignScriptedAchievementUnlocked = function(...)
 if state.synthetic then emit('test_achievement_suppressed', 'scripted'); return; end
 return originalAchievement(...);
end;
sys_LoadScenario = function(scenario)
 if state.phase == 'completed' or state.phase == 'finalizing' or state.phase == 'transitioning' then
  emit('duplicate', 'sys_LoadScenario'); return;
 end
 if type(scenario) ~= 'table' or type(scenario[1]) ~= 'string' then
  emit('failure', 'Mission supplied no next-scenario path'); return;
 end
 -- Standalone cinematic scenarios have no mission object or completion report.
 -- Their authored loader still owns score handling and the native handoff.
 if state.phase == 'idle' and g_currentMission == nil then
  state.phase = 'transitioning';
  originalLoad(scenario);
  emit('interlude_next', scenario[1]);
  return;
 end
 if state.phase == 'idle' then begin(g_currentMission); end
 state.phase = 'finalizing';
 emit('next', scenario[1]);
 -- Keep Xbox score finalization, timer handling and its authored limbo exceptions.
 -- The native game_won guard records the handoff without starting progression.
 originalLoad(scenario);
 campaign_metagame_time_pause(true);
 -- game_won is held, so the simulation remains alive beneath the local report.
 -- Extend the original Score Attack protection to normal campaign too, keeping
 -- the shared loader's authored exception for unsafe limbo during zone changes.
 if not campaign_metagame_enabled() or is_infinity_mission() then
  if scenario ~= t_scenarioTable.trials then ToggleLimboForAllPlayers(true); end
  for _, player in ipairs(players()) do
   local unit = player_get_unit(player);
   if unit ~= nil then object_cannot_die(unit, true); end
  end
 end
 emit('players_protected', 'completion report');
 emit('time', timeText(campaign_metagame_get_time_seconds()));
 emit('par', timeText(get_mission_par_time(get_campaign_mission_id())));
 emit('difficulty', difficultyText());
 emit('skulls', campaign_metagame_enabled() and ('Skull multiplier: ' .. tostring(get_total_skull_multiplier())) or '');
 local scores = {};
 if campaign_metagame_enabled() then
  for index, player in ipairs(players()) do
   scores[#scores + 1] = 'Player ' .. tostring(index) .. ': ' .. tostring(campaign_metagame_get_player_score(player));
  end
 end
 emit('scores', #scores > 0 and table.concat(scores, '    ') or (campaign_metagame_enabled() and 'Score unavailable' or ''));
 state.phase = 'completed';
 fade_in(0, 0, 0, 1);
 emit('complete', 'ready');
end;
state.TestEnd = function()
 if state.phase ~= 'idle' or g_currentMission == nil then emit('test_refused', state.phase); return; end
 state.synthetic = true;
 emit('test_begin', g_currentMission.name);
 EndCurrentMission();
end;
emit('installed', 'shared campaign adapter');
return state.Drain();
