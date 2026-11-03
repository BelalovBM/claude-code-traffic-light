"""A made-up history of the limits (limit-history.json) for pictures of the Limits tab: five finished weeks before the
ones the demo sessions fill, one run out, one reset early by Anthropic, and the 5-hour windows of the last twenty
days (the demo has no exact 5-hour figure to estimate them from). Nothing in it comes from a real account.
    python demo-history.py <data folder>"""
import json
import random
import sys
from datetime import datetime, timedelta
from pathlib import Path

random.seed(7)
now = datetime.now().replace(second=0, microsecond=0)
# The demo's exact weekly figure has its reset five days from now (stats-shot.ps1); the week before is estimated from
# the demo replies, so the kept weeks end before that.
week_end = now + timedelta(days=5)


def ms(t):
    return int(t.timestamp() * 1000)


def working(t):
    return t.weekday() < 5 and 9 <= t.hour < 20 or t.weekday() == 5 and 11 <= t.hour < 15


def week(end, peak, out_day=None, reset_day=None):
    start = end - timedelta(days=7)
    hours = [start + timedelta(hours=h) for h in range(7 * 24 + 1)]
    weights = [random.uniform(0.3, 1.6) if working(t) else 0 for t in hours]
    reset_at = start + timedelta(days=reset_day, hours=11) if reset_day is not None else None
    out_at = start + timedelta(days=out_day, hours=16) if out_day is not None else None
    # Before an early reset the week reaches 70% of its peak, after it the rest grows from zero.
    before = [w for t, w in zip(hours, weights) if reset_at is None or t < reset_at]
    total = sum(weights)
    value, curve, early_value = 0.0, [], None
    for t, w in zip(hours, weights):
        if reset_at is not None and t >= reset_at and early_value is None:
            early_value = value
            value = 0.0
        if out_at is not None and t >= out_at:
            value = 100.0
        elif reset_at is not None:
            part = sum(before) if t < reset_at else total - sum(before)
            value += w / part * (peak if t < reset_at else 0.45 * peak)
        else:
            value += w / total * peak
        curve.append((t, round(min(100.0, value), 2)))
    last = curve[-1][1]
    return {
        "StartMs": ms(start), "EndMs": ms(end), "Peak": max(v for _, v in curve), "Last": last, "Estimated": False,
        "OutAtMs": ms(out_at) if out_at else 0, "EarlyResetMs": ms(reset_at) if reset_at else 0,
        "CurveMs": [ms(t) for t, _ in curve], "CurveValues": [v for _, v in curve],
    }


weeks = [week(week_end - timedelta(days=7 * k), peak, out, reset)
         for k, peak, out, reset in [(6, 58, None, None), (5, 74, None, None), (4, 100, 5, None), (3, 85, None, 4), (2, 47, None, None)]]

fives = []
day = (now - timedelta(days=19)).replace(hour=0, minute=0)
while day < now.replace(hour=0, minute=0):
    if day.weekday() < 5 or random.random() < 0.4:
        for hour in random.sample([9, 14, 19], k=random.choice([1, 2, 2, 3])):
            start = day + timedelta(hours=hour, minutes=random.choice([0, 10, 25, 40]))
            peak = round(random.uniform(12, 92), 1)
            out = None
            if random.random() < 0.12:
                peak, out = 100.0, start + timedelta(hours=4, minutes=random.choice([5, 20, 35]))
            fives.append({"StartMs": ms(start), "EndMs": ms(start + timedelta(hours=5)), "Peak": peak, "Last": peak,
                          "Estimated": False, "OutAtMs": ms(out) if out else 0, "EarlyResetMs": 0})
    day += timedelta(days=1)

target = Path(sys.argv[1]) / "limit-history.json"
target.write_text(json.dumps({"Fives": sorted(fives, key=lambda w: w["StartMs"]), "Weeks": weeks}), encoding="utf-8")
print(target, len(weeks), "weeks,", len(fives), "windows")
