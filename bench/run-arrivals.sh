#!/bin/bash
# Arrival phases with a fixed population: six simulated players stay, a seventh arrives and leaves again.
# A player that leaves is removed from the rig's list, so the arriving one is always index 6.
T="${1:?usage: $0 <test server folder>}"
LOG="$T/BepInEx/LogOutput.log"
n=0
cmd() { n=$((n+1)); f=$(printf 'a%03d' "$n"); printf '%s' "$1" > "$T/BepInEx/bench/$f.json.tmp" && mv "$T/BepInEx/bench/$f.json.tmp" "$T/BepInEx/bench/$f.json"; sleep "${2:-1}"; }
phase() { cmd "{\"cmd\":\"label\",\"text\":\"$1\"}" 1; echo "$(date +%H:%M:%S) phase $1"; }
wait_for() {
  local start=$(grep -ac "" "$LOG"); local i=0
  while [ $i -lt "$2" ]; do
    if tail -n +$((start+1)) "$LOG" | grep -aq "$1"; then echo "$(date +%H:%M:%S)   saw '$1' after ${i}s"; return 0; fi
    sleep 1; i=$((i+1))
  done
  echo "$(date +%H:%M:%S)   no '$1' within $2 s"; return 1
}
arrival() { # label, wait pattern, max seconds
  phase "$1"
  cmd '{"cmd":"arrive"}' 1
  wait_for "$2" "$3"
  sleep 8
  cmd '{"cmd":"leave","fake":6}' 4
}

cmd '{"cmd":"churn","perSecond":0}' 1
cmd '{"cmd":"leave","fake":6}' 2
cmd '{"cmd":"leave","fake":6}' 2
cmd '{"cmd":"leave","fake":6}' 2
cmd '{"cmd":"leave","fake":6}' 2
cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true,"orderCheck":true}' 1
phase drain
sleep 45
cmd '{"cmd":"status"}' 3

cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":true}' 1
arrival A1-arrive-10KB-fair-quiet "BENCH arrival: Fake" 200
cmd '{"cmd":"set","fair":false}' 1
arrival A2-arrive-10KB-game-order-quiet "BENCH arrival: Fake" 200

cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true}' 1
arrival A3-arrive-64KB-fair-quiet "BENCH arrival: Fake" 120
cmd '{"cmd":"set","fair":false}' 1
arrival A4-arrive-64KB-game-order-quiet "BENCH arrival: Fake" 120

cmd '{"cmd":"churn","perSecond":600}' 1
cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":false}' 1
phase settle-churn
sleep 20
arrival A5-arrive-10KB-game-order-churn600 "BENCH order: Fake" 150
cmd '{"cmd":"churn","perSecond":0}' 1
phase drain2
sleep 60

cmd '{"cmd":"churn","perSecond":600}' 1
cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":true}' 1
phase settle-churn2
sleep 20
arrival A6-arrive-10KB-fair-churn600 "BENCH order: Fake" 150
cmd '{"cmd":"churn","perSecond":0}' 1

cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true,"orderCheck":false}' 1
phase idle
sleep 15
cmd '{"cmd":"status"}' 2
echo "$(date +%H:%M:%S) done"
