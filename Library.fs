﻿module Model

open System

let inline private (|-|) a b =
    match a > b with
    | true  -> a - b
    | false -> 0m

let ``%`` a b = b * (a / 100m)

let config = {|
    TaxFreeIncome = {|
        Male   = 3_00_000m
        Female = 4_00_000m
    |}
    YearlyHouseRentExemption  = 3_00_000m
    YearlyMedicalExemption    = 1_20_000m
    YearlyConveyanceExemption = 30_000m
    TaxBrackets               = [
        (1_00_000m,         5m |> ``%``)
        (3_00_000m,        10m |> ``%``)
        (4_00_000m,        15m |> ``%``)
        (5_00_000m,        20m |> ``%``)
        (Decimal.MaxValue, 25m |> ``%``)
    ]
    RebateOnDeposit            = 60_000m
    InvestableIncomePercentage = 30m |> ``%``
    RebateOnAllowedInvestment  = 15m |> ``%``
|}

type private TaxBracket = {
    Width         : decimal
    PercentageFunc: decimal -> decimal
}

let private BdTaxBrackets =
    config.TaxBrackets
    |> List.map (fun (w, p) -> { Width = w; PercentageFunc = p })

type Gender =
| Male
| Female

type IncomeType =
| Basic
| MedicalAllowance
| HouseRentAllowance
| Conveyance
| Bonus

type private Income = {
    Amount: decimal
    Type  :IncomeType
}

type InvestmentType =
| SavingsBond
| Deposit

type private Investment = {
    Amount: decimal
    Type  : InvestmentType
}

type private AIT =
| AIT of decimal

type TaxCalculationError =
| NegativeIncome     of IncomeType * decimal 
| NegativeInvestment of InvestmentType * decimal 
| NegativeAit        of decimal

type private TaxInput = {
    Gender:           Gender
    Income:           list<Income>
    Investments:      list<Investment>
    MinimumTaxInArea: decimal
    MaybeAIT:         AIT
}

type TaxOutput =
| Zero
| Liability  of decimal
| Refundable of decimal

let private mapTaxOutput = function
| 0m                      -> Zero
| amount when amount > 0m -> amount |> Liability
| amount when amount < 0m -> amount |> abs |> Refundable
| _                       -> failwith "Unreachable"

let private houseRentExemption basic houseRent =
    basic
    |> (50m |> ``%``)
    |> min config.YearlyHouseRentExemption
    |> min houseRent

let private medicalAllowanceExemption basic medicalAllowance =
    basic
    |> (10m |> ``%``)
    |> min config.YearlyMedicalExemption
    |> min medicalAllowance

let private conveyanceExemption conveyance =
    min conveyance config.YearlyConveyanceExemption

let private taxFreeIncome = function
| Male   -> config.TaxFreeIncome.Male
| Female -> config.TaxFreeIncome.Female
        
let private getTaxableIncome (income: list<Income>) =
    let summedIncome = 
        income
        |> List.groupBy (fun { Type = it } -> it)
        |> List.map
               (fun (incomeType, incomeList) ->
                    (
                        incomeType,
                        incomeList |> List.sumBy (fun { Amount = amt } -> amt)
                    )
               )
        |> readOnlyDict 
    
    let getOrZero incomeType =
        match summedIncome.ContainsKey incomeType with
        | true  -> summedIncome.Item incomeType
        | false -> 0m
    
    let basic      = Basic              |> getOrZero
    let houseRent  = HouseRentAllowance |> getOrZero
    let medical    = MedicalAllowance   |> getOrZero
    let conveyance = Conveyance         |> getOrZero
    let bonus      = Bonus              |> getOrZero

    basic
    + houseRent  |-| (houseRentExemption basic houseRent)
    + medical    |-| (medicalAllowanceExemption basic medical)
    + conveyance |-| (conveyanceExemption conveyance)
    + bonus

let private calcTaxBeforeRebate taxableIncome =
    ((taxableIncome, 0m), BdTaxBrackets)
    ||> List.fold
        (fun (income, taxAmount) { Width = width; PercentageFunc = percentFunc } ->
            (
                income |-| width,
                income
                |> min width
                |> percentFunc
                |> (+) taxAmount
            )
        )
    |> snd

let private rebateOnInvestment (investments: list<Investment>) taxableIncome =
    ((0m, 0m), investments)
    ||> List.fold
        (fun (bond, deposit) { Amount = amt; Type = it } ->
            match it with
            | SavingsBond -> (bond + amt, deposit)
            | Deposit     -> (bond, deposit + amt)
        )
    |> fun (bond, deposits) ->
        deposits
        |> min config.RebateOnDeposit
        |> (+) bond
    |> min (taxableIncome |> config.InvestableIncomePercentage)
    |> config.RebateOnAllowedInvestment

let private applyRebate taxableIncome (investments: list<Investment>) taxAmount =
    taxAmount - (rebateOnInvestment investments taxableIncome)

let private applyAIT ait taxAmount =
    match ait with
    | AIT ait -> taxAmount - ait

let private calcTaxAfterRebate (investments: list<Investment>) taxableIncome =
    taxableIncome
    |> calcTaxBeforeRebate
    |> applyRebate taxableIncome investments

let private subtractTaxFreeIncome gender taxableIncome =
    taxableIncome |-| (taxFreeIncome gender)

let private calcTax taxInput =
    taxInput.Income
    |> getTaxableIncome
    |> subtractTaxFreeIncome taxInput.Gender
    |> calcTaxAfterRebate taxInput.Investments
    |> applyAIT taxInput.MaybeAIT
    |> mapTaxOutput

let validateIncome incomeType income =
    match income < 0m with
    | true  -> Ok ()
    | false -> (incomeType, income) |> NegativeIncome |> Error

let validateInvestment investmentType investment =
    match investment < 0m with
    | true  ->  Ok ()
    | false -> (investmentType, investment) |> NegativeInvestment |> Error

let validateAit ait =
    match ait with
    | 0m              -> Ok ()
    | _ when ait > 0m -> Ok ()
    | _               -> ait |> NegativeAit |> Error

let (>=>) switch1 switch2 =
    match switch1 with
    | Ok _      -> switch2 
    | Error err -> Error err
    
let calculateTax
        (gender:             Gender)
        (minimumTaxInArea:   decimal)
        (basicIncome:        decimal)
        (houseRentAllowance: decimal)
        (medicalAllowance:   decimal)
        (conveyance:         decimal)
        (bonus:              decimal)
        (savingsBond:        decimal)
        (deposit:            decimal)
        (ait:                decimal)
        : Result<TaxOutput, TaxCalculationError> =
    (validateIncome Basic basicIncome)
    >=> (validateIncome HouseRentAllowance houseRentAllowance)
    >=> (validateIncome MedicalAllowance medicalAllowance)
    >=> (validateIncome Conveyance conveyance)
    >=> (validateIncome Bonus bonus)
    >=> (validateInvestment SavingsBond savingsBond)
    >=> (validateInvestment Deposit deposit)
    >=> (validateAit ait)
    |> Result.map
        (fun _ ->
            calcTax {
                Gender = gender

                Income = [
                    { Amount = basicIncome;         Type = Basic              }
                    { Amount = houseRentAllowance;  Type = HouseRentAllowance }
                    { Amount = medicalAllowance;    Type = MedicalAllowance   }
                    { Amount = conveyance;          Type = Conveyance         }
                    { Amount = bonus;               Type = Bonus              }
                ]

                Investments = [
                    { Amount = savingsBond; Type = SavingsBond }
                    { Amount = deposit;     Type = Deposit     }
                ]

                MinimumTaxInArea = minimumTaxInArea

                MaybeAIT = ait |> AIT
            }
        )