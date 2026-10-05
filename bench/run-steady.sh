#!/bin/bash
# Load test for the door's world-sync rule "new solid objects first, then take turns".
# Six simulated players, 20,000 floor pieces and steady changes to them. run-arrivals.sh continues from here.
T="${1:?usage: $0 <test server folder>}"
LOG="$T/BepInEx/LogOutput.log"
n=0
cmd() { n=$((n+1)); f=$(printf 'r%03d' "$n"); printf '%s' "$1" > "$T/BepInEx/bench/$f.json.tmp" && mv "$T/BepInEx/bench/$f.json.tmp" "$T/BepInEx/bench/$f.json"; sleep "${2:-1}"; }
phase() { cmd "{\"cmd\":\"label\",\"text\":\"$1\"}" 1; echo "$(date +%H:%M:%S) phase $1"; }
wait_for() { # pattern, max seconds
  local start=$(grep -ac "" "$LOG"); local i=0
  while [ $i -lt "$2" ]; do
    if tail -n +$((start+1)) "$LOG" | grep -aq "$1"; then echo "$(date +%H:%M:%S)   saw '$1' after ${i}s"; return 0; fi
    sleep 1; i=$((i+1))
  done
  echo "$(date +%H:%M:%S)   no '$1' within $2 s"; return 1
}

cmd '{"cmd":"report","every":5}' 1
cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true,"orderCheck":false}' 1
cmd '{"cmd":"fakes","count":6}' 20
cmd '{"cmd":"base","count":20000,"radius":60}' 3
cmd '{"cmd":"probes","count":20,"every":10}' 2
phase settle
sleep 40

# --- speed and frame rate, without the order check's own cost
cmd '{"cmd":"churn","perSecond":600}' 1
phase P1-defaults-600
sleep 35
cmd '{"cmd":"churn","perSecond":4000}' 1
phase P2-defaults-4000
sleep 35
cmd '{"cmd":"churn","perSecond":600}' 1
cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":true}' 1
phase P4-10KB-fair-600
sleep 45
cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":false}' 1
phase P9-10KB-game-order-600
sleep 35

# --- the same with the order check on
cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true,"orderCheck":true}' 1
phase C1-defaults-600-checked
sleep 30
cmd '{"cmd":"set","budgetKB":10,"rateKB":150,"fair":true}' 1
phase C2-10KB-fair-600-checked
sleep 30
cmd '{"cmd":"churn","perSecond":0}' 1

cmd '{"cmd":"set","budgetKB":64,"rateKB":1000,"fair":true,"orderCheck":false}' 1
phase idle
sleep 20
cmd '{"cmd":"status"}' 2
echo "$(date +%H:%M:%S) done"
