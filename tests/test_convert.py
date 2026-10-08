from datetime import date
from decimal import Decimal

import pytest

from etl.convert import Context, ConversionError, build

CTX = Context(
    code_maps={"sex": {"1": "M", "2": "F", "m": "M", "male": "M"}},
    references={"dx": {"0389", "389", "1000", "01000", "4019", "V5841", "8442"}},
)


def conv(name, args=None):
    return build(name, args, CTX)


@pytest.mark.parametrize("raw", ["20080412", "04/12/2008", "2008-04-12", "4/12/08"])
def test_date_formats(raw):
    assert conv("date", {"century": 2000})(raw) == date(2008, 4, 12)


def test_two_digit_year_without_century_is_an_error():
    with pytest.raises(ConversionError, match="century"):
        conv("date")("4/12/08")


def test_date_century_rule():
    assert conv("date", {"century": 1900})("5/1/23") == date(1923, 5, 1)


def test_bad_date():
    with pytest.raises(ConversionError):
        conv("date")("20081341")


@pytest.mark.parametrize("raw,expected", [
    ("$1,200.00", "1200.00"), ("1200", "1200.00"), ("  1200.0 ", "1200.00"),
    ("(50.00)", "-50.00"), ("-50.00", "-50.00"), ("0.00", "0.00"),
])
def test_money(raw, expected):
    assert conv("money")(raw) == Decimal(expected)


def test_money_rejects_garbage():
    with pytest.raises(ConversionError):
        conv("money")("12 dollars")


def test_integer_accepts_decimal_text_but_not_fractions():
    assert conv("integer")("12.0") == 12
    with pytest.raises(ConversionError):
        conv("integer")("12.5")


def test_code_map_case_insensitive_fallback_and_unknown():
    f = conv("code", {"map": "sex"})
    assert f("1") == "M" and f("Male") == "M" and f("M") == "M"
    with pytest.raises(ConversionError):
        f("X")


def test_id():
    assert conv("id")("  ab12 ") == "AB12"
    with pytest.raises(ConversionError):
        conv("id", {"pattern": "[0-9A-F]{4}"})("XYZ1")


@pytest.mark.parametrize("raw,expected", [
    (247037252, "00247037252"), ("00247-0372-52", "00247037252"), ("247-372-52", "00247037252"),
    ("OTHER", None),
])
def test_padded_digits(raw, expected):
    f = conv("padded_digits", {"width": 11, "segments": [5, 4, 2], "placeholders": ["OTHER"]})
    assert f(raw) == expected


def test_padded_digits_rejects_unknown_text():
    with pytest.raises(ConversionError):
        conv("padded_digits", {"width": 11})("UNKNOWN")


@pytest.mark.parametrize("raw,code,resolution", [
    ("4019", "4019", "exact"),            # valid as received; '04019' not valid
    (8442, "8442", "exact"),              # int from an INTEGER column
    (389, None, "ambiguous"),             # '389' and '0389' both valid: never guess
    (1000, None, "ambiguous"),            # '1000' and '01000' both valid
    ("0389", "0389", "exact"),            # zeros intact: nothing to resolve
    ("V58.41", "V5841", "exact"),         # dot stripped
    (12345, "12345", "not_in_reference"), # at max length: no zeros could be lost
    (77, None, "unresolved"),             # no candidate valid
])
def test_zero_padded_code(raw, code, resolution):
    out = conv("zero_padded_code", {"reference": "dx", "max_len": 5})(raw)
    assert out[""] == code and out["_resolution"] == resolution and out["_raw"] == str(raw)


def test_zero_padded_code_repairs_unique_candidate():
    ctx = Context(references={"dx": {"0389"}})
    out = build("zero_padded_code", {"reference": "dx", "max_len": 5}, ctx)(389)
    assert out == {"": "0389", "_raw": "389", "_resolution": "repaired"}


def test_assume_received_stores_labeled_guess_only_when_received_value_is_valid():
    f = conv("zero_padded_code", {"reference": "dx", "max_len": 5, "on_ambiguous": "assume_received"})
    assert f(389) == {"": "389", "_raw": "389", "_resolution": "assumed"}   # '389' valid; '0389' too
    ctx = Context(references={"dx": {"0389", "00389"}})                   # received value not valid
    g = build("zero_padded_code", {"reference": "dx", "max_len": 5, "on_ambiguous": "assume_received"}, ctx)
    assert g(389) == {"": None, "_raw": "389", "_resolution": "ambiguous"}


def test_on_ambiguous_rejects_unknown_policy():
    with pytest.raises(ValueError):
        conv("zero_padded_code", {"reference": "dx", "max_len": 5, "on_ambiguous": "pad"})
