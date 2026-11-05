import re
import networkx as nx
from .graph_models import Graph, Draft


def local_graph(snapshot: Graph, depth=1, limit=100):
    graph = nx.MultiDiGraph()
    graph.add_nodes_from(n.node_id for n in snapshot.nodes)
    for edge in snapshot.edges:
        graph.add_edge(edge.source, edge.target)
    distances = nx.single_source_shortest_path_length(graph.to_undirected(), snapshot.root, cutoff=depth)
    ids = set(sorted(distances, key=lambda n: (distances[n], n))[:limit])
    return snapshot.model_copy(update={'nodes': [n for n in snapshot.nodes if n.node_id in ids],
        'edges': [e for e in snapshot.edges if e.source in ids and e.target in ids]})


def jaccard(a, b):
    return len(a & b) / len(a | b) if a and b else None


def rank(current: Draft, previous: Draft, weights):
    root = next(n for n in current.graph.nodes if n.node_id == current.graph.root)
    old = next(n for n in previous.graph.nodes if n.node_id == previous.graph.root)
    features = []
    evidence = []
    for key in ('category', 'family', 'type_name', 'ifc_class', 'system_classification'):
        a, b = root.properties.get(key), old.properties.get(key)
        if a and b:
            features.append(float(a == b))
            evidence.append('{}: {}'.format(key, 'same' if a == b else 'different'))
    a_dims, b_dims = root.properties.get('dimensions', {}), old.properties.get('dimensions', {})
    for key in a_dims.keys() & b_dims.keys():
        a, b = a_dims[key], b_dims[key]
        if isinstance(a, (int, float)) and isinstance(b, (int, float)):
            features.append(max(0., 1 - abs(a-b) / max(abs(a), abs(b), 1.)))
    def patterns(draft, spatial):
        nodes = {n.node_id: n for n in draft.graph.nodes}
        return {(e.relationship_type, nodes[e.target].node_type,
                 str(nodes[e.target].properties.get('category', '')))
                for e in draft.graph.edges if e.source == draft.graph.root and
                e.validation_status == 'verified' and e.relationship_type != 'HAS_ISSUE' and
                ((e.relationship_type in {'CONTAINED_IN', 'ADJACENT_TO', 'LOCATED_ON_LEVEL'}) == spatial)}
    tokens = lambda text: set(re.findall(r'\w+', text.lower()))
    components = dict(element=sum(features)/len(features) if features else None,
        relationship=jaccard(patterns(current, False), patterns(previous, False)),
        spatial=jaccard(patterns(current, True), patterns(previous, True)),
        issue=.5 * float(current.issue_type == previous.issue_type) + .5 * (jaccard(tokens(current.description), tokens(previous.description)) or 0))
    applicable = sum(weights[k] for k, v in components.items() if v is not None)
    score = sum(weights[k]*v for k, v in components.items() if v is not None)/applicable if applicable else 0
    return dict(score=round(score, 4), components=components,
        completeness=round(applicable / sum(weights.values()), 4), evidence=evidence +
        ['{}: {}'.format(k, 'unavailable' if v is None else round(v, 3)) for k,v in components.items()])
