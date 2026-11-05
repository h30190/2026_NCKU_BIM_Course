"""pyRevit runs IronPython with Tracing + FullFrames. Under those options a list or set
comprehension inside a generator function fails at run time with
"Unable to cast ... FieldExpression to ... BlockExpression". Keep Revit-side
generators free of them (build the list in a helper function or a plain loop).
"""
import ast
from pathlib import Path

LIB = Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'
FORBIDDEN = (ast.ListComp, ast.SetComp)


def own_nodes(function):
    """Nodes in this function's own scope, excluding nested functions and lambdas."""
    stack = list(ast.iter_child_nodes(function))
    while stack:
        node = stack.pop()
        yield node
        if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)):
            stack.extend(ast.iter_child_nodes(node))


def offenders():
    found = []
    for path in sorted(LIB.rglob('*.py')):
        tree = ast.parse(path.read_text(encoding='utf-8-sig'), str(path))
        for function in ast.walk(tree):
            if not isinstance(function, ast.FunctionDef):
                continue
            nodes = list(own_nodes(function))
            if any(isinstance(n, (ast.Yield, ast.YieldFrom)) for n in nodes):
                found += ['{}:{} {}()'.format(path.relative_to(LIB), n.lineno, function.name)
                          for n in nodes if isinstance(n, FORBIDDEN)]
    return found


def test_revit_generators_have_no_list_or_set_comprehensions():
    assert offenders() == []
