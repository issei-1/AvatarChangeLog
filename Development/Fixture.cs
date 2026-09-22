using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class Fixture : MonoBehaviour
{
    public int number = 3;
    public long large = 9007199254740993L;
    public string text = "テスト | <tag>\n次の行";
    public GameObject reference;
    public List<float> values = new List<float> { 0.1f, 0.2f };
    public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 1);
    public Gradient gradient = new Gradient();
    public Color color = Color.white;
    public Vector2 vector2;
    public Vector4 vector4;
    public Rect rect;
    public Bounds bounds;
    public Vector2Int int2;
    public Vector3Int int3;
    public RectInt intRect;
    public BoundsInt intBounds;
    [SerializeReference] public Node node = new Node();
    [Serializable] public class Node { public float weight = 1; }
}
