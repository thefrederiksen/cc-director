#!/usr/bin/env python3
"""Generate vehicle comparison workbook spec for cc-excel from-spec.

Reads vehicle_data.json and input_defaults.json, produces a JSON spec
file that cc-excel from-spec can consume to generate the full comparison workbook.

Usage:
    python generate_car_comparison.py [DATA_DIR]
    cc-excel from-spec car-comparison-spec.json -o Vehicle_Comparison.xlsx --theme boardroom
"""
import json
import sys
from pathlib import Path
from typing import Any, Optional

COLS = list("BCDEFGHIJKLM")  # 12 vehicle columns
NUM_V = 12

# Type aliases for readability
VehicleList = list[dict[str, Any]]
SheetSpec = dict[str, Any]
RowSpec = Optional[dict[str, Any]]
CellValue = Any


def load_data(data_dir: Path) -> tuple[VehicleList, dict[str, Any]]:
    """Load vehicle and input data from JSON files."""
    with open(data_dir / "vehicle_data.json") as f:
        vehicles = json.load(f)["vehicles"]
    with open(data_dir / "input_defaults.json") as f:
        defaults = json.load(f)
    return vehicles, defaults


def vrow(i: int) -> int:
    """Excel row (1-indexed) for vehicle index i in VEHICLES sheet."""
    return i + 4


def cell(
    v: CellValue = None,
    f: Optional[str] = None,
    fmt: Optional[str] = None,
    style: Optional[str] = None,
    merge: int = 0,
    comment: Optional[str] = None,
) -> CellValue:
    """Build a cell spec dict. Returns raw value if no special properties."""
    d: dict[str, Any] = {}
    if f:
        d["f"] = f
    if fmt:
        d["fmt"] = fmt
    if style:
        d["style"] = style
    if merge > 0:
        d["merge"] = merge
    if comment:
        d["comment"] = comment
    if d:
        if v is not None:
            d["v"] = v
        return d
    return v


def type_label_cells(vehicles: VehicleList) -> list[CellValue]:
    """Build vehicle type label cells with style hints."""
    cells: list[CellValue] = ["Vehicle Type"]
    for v in vehicles:
        if v["type"] == "EV":
            cells.append(cell(v="EV", style="best"))
        elif v["type"] == "Hybrid":
            cells.append(cell(v="Hybrid", style="accent"))
        else:
            cells.append(v["type"])
    return cells


# ---------------------------------------------------------------------------
# Sheet builders
# ---------------------------------------------------------------------------

def input_sheet(defaults: dict[str, Any]) -> SheetSpec:
    """Build the INPUT sheet spec."""
    inp = defaults["inputs"]
    rts = defaults["routes"]
    rows: list[RowSpec] = [
        {"merge": 3, "value": "INPUT VARIABLES", "style": "title"},
        {"merge": 3, "value": "Change values in yellow cells to update all calculations", "style": "subtitle"},
        None,
        {"style": "header", "cells": ["Parameter", "Value", "Unit/Description"]},
        {"cells": ["Annual Driving Distance", cell(inp["annual_driving_km"]["value"], style="input"), "km/year"]},
        {"cells": ["Lease Term", cell(inp["lease_term_months"]["value"], style="input"), "months"]},
        {"cells": ["Gas Price", cell(inp["gas_price"]["value"], fmt="$#,##0.00", style="input"), "$/liter"]},
        {"cells": ["Electricity Rate", cell(inp["electricity_rate"]["value"], fmt="$#,##0.00", style="input"), "$/kWh"]},
        {"cells": ["Ontario Electricity Rebate", cell(inp["ontario_electricity_rebate"]["value"], fmt="0%", style="input"), "decimal (0.17 = 17%)"]},
        {"cells": ["Tire Swap Cost", cell(inp["tire_swap_cost"]["value"], fmt="$#,##0.00", style="input"), "$/visit"]},
        {"cells": ["Tire Swaps Per Year", cell(inp["tire_swaps_per_year"]["value"], style="input"), "times/year"]},
        {"cells": ["EV Charger Install (Toronto)", cell(inp["ev_charger_install_toronto"]["value"], fmt="$#,##0.00", style="input"), "$"]},
        {"cells": ["EV Charger Install (Cottage)", cell(inp["ev_charger_install_cottage"]["value"], fmt="$#,##0.00", style="input"), "$"]},
        None,
        {"merge": 3, "value": "ROUTE DISTANCES", "style": "subheader"},
        {"cells": ["Toronto to Cottage", cell(rts["toronto_to_cottage_km"]["value"], fmt="#,##0", style="input"), "km one-way"]},
        {"cells": ["Grey Highlands", cell(rts["grey_highlands_km"]["value"], fmt="#,##0", style="input"), "km"]},
        {"cells": ["Winter Range Buffer", cell(rts["winter_range_buffer_km"]["value"], fmt="#,##0", style="input"), "km safety margin"]},
    ]
    return {"name": "INPUT", "columns": [30, 15, 25], "freeze": [4, 0], "rows": rows}


def vehicles_sheet(vehicles: VehicleList) -> SheetSpec:
    """Build the VEHICLES sheet spec."""
    headers = [
        "Vehicle", "Type", "Monthly Lease", "Battery (kWh)", "Fuel Tank (L)",
        "Efficiency", "Annual Service", "Annual Insurance", "Winter Tires",
        "DC Fast Rate", "Annual Public Charging", "Winter Range (km)",
    ]
    rows: list[RowSpec] = [
        {"merge": len(headers), "value": "VEHICLE SPECIFICATIONS", "style": "title"},
        None,
        {"style": "header", "cells": headers},
    ]
    for v in vehicles:
        rows.append({"cells": [
            v["name"],
            v["type"],
            cell(v["monthly_lease"], fmt="$#,##0"),
            cell(v["battery_kwh"], fmt="#,##0") if v["battery_kwh"] else "-",
            cell(v["fuel_tank_liters"], fmt="#,##0") if v["fuel_tank_liters"] else "-",
            f'{v["efficiency"]} {v["efficiency_unit"]}',
            cell(v["annual_service"], fmt="$#,##0"),
            cell(v["annual_insurance"], fmt="$#,##0"),
            cell(v["winter_tire_set"], fmt="$#,##0"),
            cell(v["dc_fast_charge_rate"], fmt="$#,##0.00") if v["dc_fast_charge_rate"] else "-",
            cell(v["annual_public_charging"], fmt="$#,##0"),
            cell(v["winter_range_km"], fmt="#,##0"),
        ]})
    return {
        "name": "VEHICLES",
        "columns": [18, 8, 14, 14, 14, 20, 14, 14, 14, 14, 20, 16],
        "freeze": [3, 0],
        "rows": rows,
    }


def _build_monthly_formula_rows(vehicles: VehicleList) -> list[list[CellValue]]:
    """Build the 6 formula rows for monthly costs (lease through total)."""
    lease: list[CellValue] = ["Lease Payment"]
    fuel: list[CellValue] = ["Fuel/Electricity"]
    service: list[CellValue] = ["Service"]
    insurance: list[CellValue] = ["Insurance"]
    tires: list[CellValue] = ["Tire Swaps"]
    total: list[CellValue] = ["TOTAL MONTHLY"]

    for i, v in enumerate(vehicles):
        col = COLS[i]
        eff = v["efficiency"]
        lease.append(cell(f=f"=VEHICLES!C{vrow(i)}", fmt="$#,##0.00"))
        if v["type"] == "EV":
            fuel.append(cell(f=f"=(INPUT!$B$5/12)*({eff}/100)*INPUT!$B$8*(1-INPUT!$B$9)", fmt="$#,##0.00"))
        else:
            fuel.append(cell(f=f"=(INPUT!$B$5/12)*({eff}/100)*INPUT!$B$7", fmt="$#,##0.00"))
        service.append(cell(f=f"=VEHICLES!G{vrow(i)}/12", fmt="$#,##0.00"))
        insurance.append(cell(f=f"=VEHICLES!H{vrow(i)}/12", fmt="$#,##0.00"))
        tires.append(cell(f="=INPUT!$B$10*INPUT!$B$11/12", fmt="$#,##0.00"))
        total.append(cell(f=f"=SUM({col}6:{col}10)", fmt="$#,##0.00"))

    return [lease, fuel, service, insurance, tires, total]


def monthly_costs_sheet(vehicles: VehicleList) -> SheetSpec:
    """Build the MONTHLY COSTS sheet spec with live formulas."""
    ncols = NUM_V + 1
    names = [v["name"] for v in vehicles]
    formula_rows = _build_monthly_formula_rows(vehicles)

    rows: list[RowSpec] = [
        {"merge": ncols, "value": "MONTHLY COST COMPARISON", "style": "title"},
        {"merge": ncols, "value": "All values calculated from INPUT and VEHICLES tabs", "style": "subtitle"},
        None,
        {"style": "header", "cells": ["Cost Category"] + names},
        {"style": "subheader", "cells": type_label_cells(vehicles)},
    ]
    for fr in formula_rows[:-1]:
        rows.append({"cells": fr})
    rows.append({"style": "total", "cells": formula_rows[-1]})

    return {
        "name": "MONTHLY COSTS",
        "columns": [22] + [16] * NUM_V,
        "freeze": [5, 1],
        "rows": rows,
    }


def _build_3year_cost_rows(vehicles: VehicleList) -> list[dict[str, Any]]:
    """Build the 7 cost category rows for 3-year totals."""
    mc_refs = [
        ("Lease Payments", 6), ("Fuel/Electricity", 7), ("Service", 8),
        ("Insurance", 9), ("Tire Swaps", 10),
    ]
    cost_rows: list[dict[str, Any]] = []
    for label, mc_row in mc_refs:
        cells: list[CellValue] = [label]
        for i in range(NUM_V):
            col = COLS[i]
            cells.append(cell(f=f"='MONTHLY COSTS'!{col}{mc_row}*INPUT!$B$6", fmt="$#,##0"))
        cost_rows.append({"cells": cells})

    tire_cells: list[CellValue] = ["Winter Tire Set"]
    for i in range(NUM_V):
        tire_cells.append(cell(f=f"=VEHICLES!I{vrow(i)}", fmt="$#,##0"))
    cost_rows.append({"cells": tire_cells})

    charger_cells: list[CellValue] = ["EV Charger Install"]
    for i, v in enumerate(vehicles):
        if v["type"] == "EV":
            charger_cells.append(cell(f="=INPUT!$B$12+INPUT!$B$13", fmt="$#,##0"))
        else:
            charger_cells.append(cell(0, fmt="$#,##0"))
    cost_rows.append({"cells": charger_cells})

    return cost_rows


def three_year_sheet(vehicles: VehicleList) -> SheetSpec:
    """Build the 3-YEAR TOTAL sheet spec with live formulas."""
    ncols = NUM_V + 1
    names = [v["name"] for v in vehicles]
    cost_rows = _build_3year_cost_rows(vehicles)

    total: list[CellValue] = ["TOTAL 3-YEAR COST"]
    savings: list[CellValue] = ["Savings vs Most Expensive"]
    for i in range(NUM_V):
        col = COLS[i]
        total.append(cell(f=f"=SUM({col}6:{col}12)", fmt="$#,##0"))
        savings.append(cell(f=f"=MAX(B13:M13)-{col}13", fmt="$#,##0"))

    rows: list[RowSpec] = [
        {"merge": ncols, "value": "3-YEAR TOTAL COST OF OWNERSHIP", "style": "title"},
        {"merge": ncols, "value": "Based on lease term from INPUT tab", "style": "subtitle"},
        None,
        {"style": "header", "cells": ["Cost Category"] + names},
        {"style": "subheader", "cells": type_label_cells(vehicles)},
    ] + cost_rows + [
        {"style": "total", "cells": total},
        None,
        {"style": "subheader", "cells": savings},
    ]
    return {
        "name": "3-YEAR TOTAL",
        "columns": [28] + [16] * NUM_V,
        "freeze": [5, 1],
        "rows": rows,
    }


def _build_rank_data_rows(vehicles: VehicleList) -> list[list[CellValue]]:
    """Build the 4 ranking category rows plus average and overall."""
    monthly_rank: list[CellValue] = ["Rank: Monthly Cost"]
    year3_rank: list[CellValue] = ["Rank: 3-Year Total"]
    service_rank: list[CellValue] = ["Rank: Service Cost"]
    avg_rank: list[CellValue] = ["AVERAGE RANK"]
    overall_rank: list[CellValue] = ["OVERALL RANK"]

    for i in range(NUM_V):
        col = COLS[i]
        monthly_rank.append(cell(f=f"=RANK('MONTHLY COSTS'!{col}11,'MONTHLY COSTS'!B11:M11,1)"))
        year3_rank.append(cell(f=f"=RANK('3-YEAR TOTAL'!{col}13,'3-YEAR TOTAL'!B13:M13,1)"))
        service_rank.append(cell(f=f"=RANK(VEHICLES!G{vrow(i)},VEHICLES!G4:G15,1)"))
        avg_rank.append(cell(f=f"=AVERAGE({col}5:{col}8)", fmt="0.00"))
        overall_rank.append(cell(f=f"=RANK({col}10,B10:M10,1)"))

    # Winter range ranks: Gas/Hybrid=1, EVs ranked by descending range
    ev_ranges = [(i, v["winter_range_km"]) for i, v in enumerate(vehicles) if v["type"] == "EV"]
    ev_ranges.sort(key=lambda x: -x[1])
    winter_ranks: list[int] = [1] * NUM_V
    for offset, (idx, _) in enumerate(ev_ranges):
        winter_ranks[idx] = offset + 2
    range_rank: list[CellValue] = ["Rank: Winter Range"] + winter_ranks

    return [monthly_rank, year3_rank, range_rank, service_rank, avg_rank, overall_rank]


def _build_ranking_cfs() -> list[dict[str, Any]]:
    """Build color scale conditional formatting rules for ranking rows."""
    return [
        {"range": f"B{r}:M{r}", "type": "2_color_scale", "min_color": "#63BE7B", "max_color": "#F8696B"}
        for r in [5, 6, 7, 8, 10, 11]
    ]


def rankings_sheet(vehicles: VehicleList) -> SheetSpec:
    """Build the RANKINGS sheet spec with RANK formulas and conditional formatting."""
    ncols = NUM_V + 1
    names = [v["name"] for v in vehicles]
    rank_rows = _build_rank_data_rows(vehicles)

    rows: list[RowSpec] = [
        {"merge": ncols, "value": "RANKINGS - SUMMARY SCORECARD", "style": "title"},
        None,
        {"style": "header", "cells": ["Category"] + names},
        {"style": "subheader", "cells": type_label_cells(vehicles)},
        {"cells": rank_rows[0]},
        {"cells": rank_rows[1]},
        {"cells": rank_rows[2]},
        {"cells": rank_rows[3]},
        None,
        {"style": "total", "cells": rank_rows[4]},
        {"style": "total", "cells": rank_rows[5]},
        None,
        None,
        {"merge": ncols, "value": "KEY INSIGHTS", "style": "subheader"},
        {"merge": ncols, "value": "1. Rank 1 = Best (lowest cost, best range, lowest service cost)"},
        {"merge": ncols, "value": "2. Average Rank combines all 4 categories equally"},
        {"merge": ncols, "value": "3. Winter Range: Gas/Hybrid vehicles all rank 1 (no range anxiety)"},
        {"merge": ncols, "value": "4. Color scale: Green = best ranking, Red = worst ranking"},
        {"merge": ncols, "value": "5. Change INPUT values to see how rankings shift"},
    ]
    return {
        "name": "RANKINGS",
        "columns": [25] + [16] * NUM_V,
        "freeze": [4, 1],
        "rows": rows,
        "conditional_formats": _build_ranking_cfs(),
    }


def route_sheet(vehicles: VehicleList) -> SheetSpec:
    """Build the ROUTE ANALYSIS sheet spec (pre-computed values)."""
    evs = [(v["name"], v["winter_range_km"]) for v in vehicles if v["type"] == "EV"]
    cottage_dist = 250
    grey_round = 360  # 180 km one-way * 2

    rows: list[RowSpec] = [
        {"merge": 3, "value": "EV RANGE ANALYSIS FOR KEY ROUTES", "style": "title"},
        None,
        {"merge": 3, "value": "YOUR CHARGING SETUP", "style": "subheader"},
        {"cells": ["Toronto Home", "Level 2 Charger", cell("Full charge overnight", style="best")]},
        {"cells": ["Parry Sound Cottage", "Level 2 Charger", cell("Full charge overnight", style="best")]},
        None,
        {"merge": 3, "value": "GAS/HYBRID VEHICLES", "style": "subheader"},
        {"merge": 3, "value": "No range considerations - gas stations available on all routes"},
        None,
        {"merge": 3, "value": "EV RANGE ANALYSIS", "style": "subheader"},
        {"style": "header", "cells": ["Route / Vehicle", "Assessment", "Details"]},
    ]

    for name, winter_range in evs:
        rows.append({"merge": 3, "value": f"{name} (Winter Range: {winter_range} km)", "style": "subheader"})
        for route_name, dist in [
            (f"Toronto to Cottage ({cottage_dist} km)", cottage_dist),
            (f"Grey Highlands Day Trip ({grey_round} km round trip)", grey_round),
        ]:
            buf = winter_range - dist
            if buf >= 50:
                assess, sty, detail = "NO PROBLEM", "best", f"{buf} km buffer remaining"
            elif buf >= 0:
                assess, sty, detail = "DOABLE - tight", None, f"Only {buf} km buffer"
            else:
                assess, sty, detail = "QUICK CHARGE NEEDED", "worst", f"{abs(buf)} km short"
            rows.append({"cells": [route_name, cell(assess, style=sty), detail]})
        rows.append(None)

    rows.extend([
        {"merge": 3, "value": "BOTTOM LINE", "style": "subheader"},
        {"merge": 3, "value": "Cadillac Lyriq handles all routes comfortably. Kia EV6 good for cottage, needs charge for Grey Highlands."},
        {"merge": 3, "value": "Toyota bZ4X cannot reach cottage without charging. Ford Mach-E good for cottage, tight for Grey Highlands."},
    ])
    return {"name": "ROUTE ANALYSIS", "columns": [55, 20, 25], "rows": rows}


def interview_sheet() -> SheetSpec:
    """Build the INTERVIEW DATA sheet spec (static reference)."""
    rows: list[RowSpec] = [
        {"merge": 2, "value": "INTERVIEW DATA - REFERENCE", "style": "title"},
        None,
        {"merge": 2, "value": "DRIVER PROFILE", "style": "subheader"},
        {"cells": ["Primary Driver", "Gitte"]},
        {"cells": ["Residence", "Toronto (primary), Parry Sound cottage"]},
        {"cells": ["Driving Pattern", "Weekend cottage trips, local city driving"]},
        {"cells": ["Annual Mileage Est.", "15,000-20,000 km"]},
        {"cells": ["Vehicle Registration", "Parry Sound (lower insurance rates)"]},
        None,
        {"merge": 2, "value": "CHARGING INFRASTRUCTURE", "style": "subheader"},
        {"cells": ["Toronto Home", "Requires Level 2 charger installation (~$2,500)"]},
        {"cells": ["Cottage", "Pre-wired, needs connection (~$700)"]},
        {"cells": ["Route Charging", "Gravenhurst Supercharger, various DC fast chargers"]},
        {"cells": ["Typical Charge Time", "30-45 min DC fast charge, overnight at home"]},
        None,
        {"merge": 2, "value": "KEY CONSIDERATIONS", "style": "subheader"},
        {"cells": ["Winter Range", "Must handle 250 km cottage trip in winter conditions"]},
        {"cells": ["Insurance", "Registration in Parry Sound reduces rates"]},
        {"cells": ["Service Costs", "EVs significantly cheaper to service"]},
        {"cells": ["Total Cost", "3-year lease total is primary comparison metric"]},
        {"cells": ["Resale/Buyout", "Lease residual values vary significantly"]},
        None,
        {"merge": 2, "value": "DEALERSHIP CONTACTS", "style": "subheader"},
        {"cells": ["Cadillac", "Local dealer, Lyriq and XT5 available"]},
        {"cells": ["Kia", "EV6 and Sorento available"]},
        {"cells": ["Volvo", "XC60 B5 AWD available"]},
        {"cells": ["Lexus", "RX 350h, Mark (sales), 4.9% lease rate"]},
        {"cells": ["Ford", "Mustang Mach-E available"]},
        {"cells": ["Lincoln", "Nautilus available"]},
    ]
    return {"name": "INTERVIEW DATA", "columns": [25, 60], "rows": rows}


def _build_safety_data_rows(
    sv: list[str],
    nhtsa: list[Optional[int]],
    award: list[str],
    notable: list[str],
) -> list[list[CellValue]]:
    """Build the 3 data rows for safety ratings (NHTSA, IIHS, notable)."""
    nhtsa_cells: list[CellValue] = ["NHTSA Overall (5-Star)"]
    for r in nhtsa:
        if r == 5:
            nhtsa_cells.append(cell(f"{r} Stars", style="best"))
        elif r:
            nhtsa_cells.append(f"{r} Stars")
        else:
            nhtsa_cells.append("-")

    iihs_cells: list[CellValue] = ["IIHS Safety Award"]
    for a in award:
        if "Top Safety Pick" in a:
            iihs_cells.append(cell(a, style="best"))
        elif a == "None":
            iihs_cells.append(cell(a, style="worst"))
        else:
            iihs_cells.append(a)

    notable_cells: list[CellValue] = ["Notable"] + notable
    return [nhtsa_cells, iihs_cells, notable_cells]


def safety_sheet() -> SheetSpec:
    """Build the SAFETY RATINGS sheet spec (summary view)."""
    sv = [
        "Cadillac Lyriq", "Kia EV6", "Cadillac XT5", "Kia Sorento",
        "Volvo XC60", "Lexus RX 350h", "Ford Mustang Mach-E",
        "Lincoln Nautilus", "Buick Envision",
    ]
    nhtsa: list[Optional[int]] = [5, 5, 5, 5, 5, 5, 5, None, 5]
    award = [
        "None", "None", "Former TSP+", "Top Safety Pick", "Top Safety Pick",
        "Lost TSP 2025", "Top Safety Pick", "Partial Testing", "None",
    ]
    notable = [
        "Poor headlights", "Marginal moderate overlap", "Marginal headlights",
        "Marginal moderate overlap", "All Good IIHS ratings", "Best reliability",
        "All Good IIHS, most reliable EV", "MotorTrend 2025 SUV of Year",
        "Acceptable headlights",
    ]
    ncols = len(sv) + 1
    data_rows = _build_safety_data_rows(sv, nhtsa, award, notable)

    rows: list[RowSpec] = [
        {"merge": ncols, "value": "VEHICLE SAFETY RATINGS", "style": "title"},
        {"merge": ncols, "value": "Data from NHTSA (nhtsa.gov) and IIHS (iihs.org)", "style": "subtitle"},
        None,
        {"style": "header", "cells": ["Category"] + sv},
    ]
    for dr in data_rows:
        rows.append({"cells": dr})
    rows.extend([
        None,
        {"merge": ncols, "value": "COLOR LEGEND", "style": "subheader"},
        {"merge": ncols, "value": "Green = Top rating or award  |  Red = No award or poor rating"},
        None,
        {"merge": ncols, "value": "NOTE: Toyota bZ4X, Crown Signia, and Prius not included in safety summary"},
    ])
    return {
        "name": "SAFETY RATINGS",
        "columns": [25] + [18] * len(sv),
        "freeze": [3, 1],
        "rows": rows,
    }


# Models not included in comparison (name, type, why_researched, why_not)
_NOT_INCLUDED = [
    ("Toyota RAV4 AWD", "Gas", "Popular SUV", "Less refined than shortlisted options"),
    ("Hyundai Ioniq 5 AWD", "EV", "Award-winning EV", "Higher insurance, supply constraints"),
    ("Hyundai Santa Fe AWD", "Gas", "Practical SUV", "Less premium feel"),
    ("Volvo EX40", "EV", "Premium compact EV", "Small for needs, limited range"),
    ("Genesis GV60 AWD", "EV", "Luxury compact EV", "Too small, limited dealer network"),
    ("Genesis Electrified GV70", "EV", "Luxury EV SUV", "Very expensive lease"),
    ("BMW iX xDrive50", "EV", "Premium EV", "Very expensive, over budget"),
    ("Mercedes EQE SUV AWD", "EV", "Premium EV", "Very expensive, over budget"),
    ("Audi Q8 e-tron", "EV", "Premium EV", "Expensive, older platform"),
    ("Chevrolet Equinox EV AWD", "EV", "Budget EV", "Too new, unproven reliability"),
    ("Nissan Ariya e-4ORCE", "EV", "Mid-range EV", "Supply issues, slow charging"),
    ("VW ID.4 AWD", "EV", "Mid-range EV", "Software issues, slow charging"),
    ("Polestar 3", "EV", "Premium EV", "Not yet available in Canada"),
    ("Rivian R1S", "EV", "Adventure EV", "Very expensive, limited service"),
    ("Subaru Solterra AWD", "EV", "Budget EV", "Same platform as bZ4X, limited range"),
    ("Jeep Grand Cherokee 4xe", "PHEV", "Plug-in hybrid", "Reliability concerns"),
    ("Acura MDX AWD", "Gas", "Premium SUV", "Higher fuel consumption"),
    ("Mazda CX-90 PHEV", "PHEV", "Plug-in hybrid", "Complex drivetrain, mixed reviews"),
    ("Honda CR-V Hybrid AWD", "Hybrid", "Efficient hybrid", "Less premium, smaller"),
    ("Subaru Outback AWD", "Gas", "Reliable wagon", "Lower premium feel"),
]

# Models added to comparison (name, type, why_researched, why_added)
_ADDED = [
    ("Volvo XC60 B5 AWD", "Gas", "Premium SUV", "Top Safety Pick, best gas reliability"),
    ("Lexus RX 350h AWD", "Hybrid", "Premium hybrid", "Best overall reliability"),
    ("Ford Mustang Mach-E AWD", "EV", "Mid-range EV", "Most reliable EV (Consumer Reports)"),
    ("Lincoln Nautilus Hybrid", "Gas", "Premium SUV", "MotorTrend 2025 SUV of Year"),
]


def other_models_sheet() -> SheetSpec:
    """Build the OTHER MODELS sheet spec."""
    rows: list[RowSpec] = [
        {"merge": 4, "value": "OTHER MODELS RESEARCHED", "style": "title"},
        {"merge": 4, "value": "Models evaluated during the vehicle selection process", "style": "subtitle"},
        None,
        {"merge": 4, "value": "MODELS NOT ADDED TO COMPARISON", "style": "subheader"},
        {"style": "header", "cells": ["Vehicle", "Type", "Why Researched", "Why Not Included"]},
    ]
    for name, vtype, why_res, why_not in _NOT_INCLUDED:
        rows.append({"cells": [name, vtype, why_res, why_not], "height": 30})
    rows.extend([
        None,
        {"merge": 4, "value": "MODELS ADDED TO COMPARISON", "style": "subheader"},
        {"style": "header", "cells": ["Vehicle", "Type", "Why Researched", "Why Added"]},
    ])
    for name, vtype, why_res, why_add in _ADDED:
        rows.append({
            "cells": [cell(name, style="best"), vtype, why_res, cell(why_add, style="best")],
            "height": 30,
        })
    rows.extend([
        None,
        {"merge": 4, "value": "RESEARCH SUMMARY", "style": "subheader"},
        {"merge": 4, "value": "Total vehicles researched: 25+ models across EV, Hybrid, and Gas categories"},
        {"merge": 4, "value": "Final shortlist: 12 vehicles selected for detailed comparison"},
        {"merge": 4, "value": "Key finding: Hybrid and efficient gas vehicles often beat EVs on total cost"},
    ])
    return {"name": "OTHER MODELS", "columns": [30, 22, 35, 50], "freeze": [4, 0], "rows": rows}


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def build_spec(vehicles: VehicleList, defaults: dict[str, Any]) -> dict[str, Any]:
    """Build the complete workbook spec."""
    return {
        "theme": "boardroom",
        "sheets": [
            input_sheet(defaults),
            vehicles_sheet(vehicles),
            monthly_costs_sheet(vehicles),
            three_year_sheet(vehicles),
            rankings_sheet(vehicles),
            route_sheet(vehicles),
            interview_sheet(),
            safety_sheet(),
            other_models_sheet(),
        ],
    }


def main() -> None:
    """Generate JSON spec from vehicle data. Optional arg: data directory path."""
    default_dir = Path(r"D:\Personal\OneDrive\Documents\Gitte\New Car")
    data_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else default_dir

    vehicles, defaults = load_data(data_dir)
    spec = build_spec(vehicles, defaults)
    out_path = Path(__file__).parent / "car-comparison-spec.json"
    with open(out_path, "w") as f:
        json.dump(spec, f, indent=2)
    print(f"Spec written to {out_path}")
    print(f"Run: cc-excel from-spec \"{out_path}\" -o output.xlsx --theme boardroom")


if __name__ == "__main__":
    main()
