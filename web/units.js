'use strict';
/* The two games (Euro Truck Simulator 2, American Truck Simulator) and the units each shows (port of Game.cs). */

const Games = {
  ETS2: { id: 'ets2', name: 'Euro Truck Simulator 2', short: 'ETS2', imperial: false, currency: '€', scale: 19 },
  ATS: { id: 'ats', name: 'American Truck Simulator', short: 'ATS', imperial: true, currency: '$', scale: 20 },
  current: null,
  byId(id) { return id === 'ats' ? Games.ATS : Games.ETS2; },
  // the telemetry's game name ("ETS2", "ATS"), or null when it says nothing
  fromName(name) {
    if (!name) return null;
    const n = String(name).toUpperCase();
    return n.includes('ATS') || n.includes('AMERICAN') ? Games.ATS : n.includes('ETS') || n.includes('EURO') ? Games.ETS2 : null;
  },
};
Games.current = (() => { try { return Games.byId(JSON.parse(localStorage.getItem('ets2gps.game'))); } catch (e) { return Games.ETS2; } })();

// Numbers as the current game shows them. Inputs are metric (game metres, km, km/h, litres, kg), as the telemetry and the map give them.
// The user's choice in Settings: 'game' (km in ETS2, miles in ATS), 'metric' or 'imperial'.
let unitChoice = (() => { try { return JSON.parse(localStorage.getItem('ets2gps.units')) || 'game'; } catch (e) { return 'game'; } })();
const Units = {
  get choice() { return unitChoice; },
  set choice(v) { unitChoice = v; try { localStorage.setItem('ets2gps.units', JSON.stringify(v)); } catch (e) { /* private mode */ } },
  get imperial() { return unitChoice === 'imperial' || (unitChoice !== 'metric' && Games.current.imperial); },
  get speedUnit() { return Units.imperial ? 'mph' : 'km/h'; },
  get distUnit() { return Units.imperial ? 'mi' : 'km'; },
  get money() { return Games.current.currency; },
  speed(kmh) { return Units.imperial ? kmh / 1.609344 : kmh; },
  long(km) { return Units.imperial ? km / 1.609344 : km; },
  fuel(l) { return Units.imperial ? l / 3.785411784 : l; },
  alt(m) { return Units.imperial ? m / 0.3048 : m; },
  n(v, d = 0) { return Number(v).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }); },
  longText(km) { return Units.n(Units.long(km)) + ' ' + Units.distUnit; },
  speedText(kmh) { return Math.round(Math.max(0, Units.speed(kmh))) + ' ' + Units.speedUnit; },
  altText(m) { return Math.round(Units.alt(m)) + (Units.imperial ? ' ft' : ' m'); },
  fuelText(l) { return Units.n(Units.fuel(l)) + (Units.imperial ? ' gal' : ' L'); },
  mass(kg) { return Units.imperial ? Units.n(Math.round(kg * 2.20462262 / 100) * 100) + ' lb' : (kg / 1000).toFixed(1) + ' t'; },
  consumption(lPerKm) {
    if (!Units.imperial) return (lPerKm * 100).toFixed(1) + ' L/100 km';
    return (lPerKm > 0 ? (1.609344 / (lPerKm * 3.785411784)).toFixed(1) : '-') + ' mpg';
  },
  // short distance for the screen, from metres
  short(m) {
    if (!Units.imperial) {
      if (m < 1000) return Math.max(10, Math.round(m / 10) * 10) + ' m';
      if (m < 10000) return (m / 1000).toFixed(1) + ' km';
      return Math.round(m / 1000) + ' km';
    }
    const mi = m / 1609.344;
    if (mi < 0.1) return Math.max(50, Math.round(m / 0.3048 / 50) * 50) + ' ft';
    if (mi < 10) return mi.toFixed(1) + ' mi';
    return Math.round(mi) + ' mi';
  },
  // distance as spoken, from metres
  spoken(m) {
    if (!Units.imperial) {
      if (m < 950) return Math.max(50, Math.round(m / 50) * 50) + ' meters';
      if (m < 10000) return Math.round(m / 500) / 2 + ' kilometers';
      return Math.round(m / 1000) + ' kilometers';
    }
    const mi = m / 1609.344;
    if (mi < 0.19) return Math.max(100, Math.round(m / 0.3048 / 100) * 100) + ' feet';
    if (mi < 0.38) return 'a quarter mile';
    if (mi < 0.63) return 'half a mile';
    if (mi < 0.88) return 'three quarters of a mile';
    if (mi < 1.25) return '1 mile';
    if (mi < 10) return Math.round(mi * 2) / 2 + ' miles';
    return Math.round(mi) + ' miles';
  },
};
